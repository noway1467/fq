using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FeiqLight
{
    public sealed partial class LanService
    {
        private const int BatchFileLimit = 64, BatchFileIdleMilliseconds = 1500, BatchRequestMilliseconds = 3000;
        private sealed class FileConnection
        {
            internal TcpClient Client;
            internal SslStream Stream;
            internal string DeviceId, Fingerprint;
            internal IPEndPoint Endpoint;
            internal int Epoch, Files;
            internal TimeSpan IdleSince;
            internal Timer IdleTimer;
        }
        private FileConnection idleFileConnection;
        private readonly Dictionary<TcpClient,string> fileBatchPeers = new Dictionary<TcpClient,string>();

        private FileConnection CreateFileConnection(TcpClient client, SslStream stream, Peer peer)
        {
            FileConnection channel=new FileConnection{Client=client,Stream=stream,DeviceId=peer.DeviceId,Fingerprint=peer.Fingerprint,
                Endpoint=(IPEndPoint)client.Client.RemoteEndPoint,Epoch=TransportEpoch};
            channel.IdleTimer=new Timer(_=>{lock(gate)if(idleFileConnection==channel&&
                (clock.Elapsed-channel.IdleSince).TotalMilliseconds>=BatchFileIdleMilliseconds)CloseFileConnection(channel);},null,Timeout.Infinite,Timeout.Infinite);
            lock(gate)fileBatchPeers[client]=peer.DeviceId;
            return channel;
        }

        private void CloseFileConnection(FileConnection channel)
        {
            if (channel == null) return;
            lock (gate)
            {
                if (idleFileConnection == channel) idleFileConnection = null;
                if (channel.IdleTimer != null) channel.IdleTimer.Dispose();
                channel.Client.Close();
                try { channel.Stream.Dispose(); } catch (IOException) { }
                ReleaseConnection(channel.Client); fileBatchPeers.Remove(channel.Client);
            }
        }
        private void CloseFileBatches(string deviceId)
        {
            lock (gate)
            {
                foreach (TcpClient client in fileBatchPeers.Where(item => deviceId == null || item.Value == deviceId).Select(item => item.Key).ToList()) client.Close();
                if (idleFileConnection != null && (deviceId == null || idleFileConnection.DeviceId == deviceId)) CloseFileConnection(idleFileConnection);
            }
        }
        private FileConnection TakeFileConnection(Peer peer)
        {
            lock (gate)
            {
                FileConnection channel = idleFileConnection;
                if (channel == null) return null;
                idleFileConnection = null;
                if (disposed || channel.Epoch != transportEpoch || channel.DeviceId != peer.DeviceId || channel.Fingerprint != peer.Fingerprint ||
                    !channel.Endpoint.Equals(peer.Endpoint) || !channel.Client.Connected || !Security.IsTrusted(peer.DeviceId, peer.Fingerprint))
                { CloseFileConnection(channel); return null; }
                channel.IdleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                return channel;
            }
        }
        private void ReturnFileConnection(FileConnection channel, CancellationToken cancel)
        {
            lock (gate)
            {
                if (disposed || cancel.IsCancellationRequested || channel.Files >= BatchFileLimit || channel.Epoch != transportEpoch ||
                    !channel.Client.Connected || !Security.IsTrusted(channel.DeviceId, channel.Fingerprint)) { CloseFileConnection(channel); return; }
                if (idleFileConnection != null) CloseFileConnection(idleFileConnection);
                idleFileConnection = channel;
                channel.IdleSince=clock.Elapsed;
                channel.IdleTimer.Change(BatchFileIdleMilliseconds, Timeout.Infinite);
            }
        }
        private void SendReleaseFiles(Peer peer, string body)
        {
            RequireSecurePeer(peer.DeviceId,peer.Fingerprint);
            TcpClient connection = null;
            try
            {
                using (TcpClient client = connection = SecurePeerConnect(peer))
                using (SslStream stream = Security.Client(client, peer.Fingerprint))
                {
                    Packet remote = AuthenticateSecureClient(stream, peer.DeviceId, peer.Fingerprint, peer.DisplayName);
                    SecureIdentity.WriteText(stream, "M\n" + Port);
                    SecureIdentity.WriteFrame(stream, Encode(Protocol.ReleaseFiles, body, null));
                    if (SecureIdentity.ReadText(stream, 1024) != "OK") throw new IOException("文件通知未送达。");
                    Upsert(remote, peer.Endpoint);
                }
            }
            finally { if (connection != null) ReleaseConnection(connection); }
        }
        public void RejectFiles(Peer peer, long packet)
        {
            string body = packet.ToString(CultureInfo.InvariantCulture);
            Peer snapshot = new Peer {
                Endpoint = new IPEndPoint(peer.Endpoint.Address, peer.Endpoint.Port), DeviceId = peer.DeviceId, Fingerprint = peer.Fingerprint, Login = peer.Login,
                Nickname = peer.DisplayName
            };
            RequireSecurePeer(snapshot.DeviceId,snapshot.Fingerprint);
            Task.Run(() => { try { SendReleaseFiles(snapshot, body); } catch (Exception e) { Report("已本地拒收并清理断点；拒绝通知未送达：" + e.Message); } });
        }
        public Task<string> ReceiveFileAsync(Peer peer, long packet, Attachment file, string destination, IProgress<int> progress, CancellationToken cancel,
            bool renameOnCollision = false, IProgress<TransferProgress> details = null, Func<bool> keepPartial = null)
        {
            RequireSecurePeer(peer.DeviceId,peer.Fingerprint);
            return Task.Run(()=>ReceiveSecureFile(peer,packet,file,destination,progress,cancel,renameOnCollision,details,keepPartial),cancel);
        }
        public static string PartialPath(Peer peer, long packet, Attachment file, string destination)
        {
            string path = Path.GetFullPath(destination);
            string identity = peer.Id + "\n" + peer.Host + "\n" + packet + "\n" + file.Id + "\n" + file.Name + "\n" + file.Size + "\n" + file.Modified + "\n" + path.ToUpperInvariant();
            using (SHA256 sha = SHA256.Create())
                return Path.Combine(Path.GetDirectoryName(path), ".feiq-" + BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity))).Replace("-", "") + ".part");
        }
        private string ReceiveSecureFile(Peer peer,long packet,Attachment file,string destination,IProgress<int> progress,CancellationToken cancel,bool rename,
            IProgress<TransferProgress> details,Func<bool> keepPartial)
        {
            if(!Protocol.SafeFileName(file.Name)||file.Size<0||!SecureIdentity.ValidFingerprint(file.Sha256))throw new IOException("文件元数据或校验值无效。");
            string path=Path.GetFullPath(destination),part=PartialPath(peer,packet,file,path);
            bool owned=false,revoked=false;
            bool reuseConnection=false;
            bool borrowedConnection=false;
            FileConnection channel=null;
            TcpClient connection=null;
            SslStream directStream=null;
            PendingDownload pd=null;
            SemaphoreSlim targetLock=receiveLocks[(path.ToUpperInvariant().GetHashCode()&Int32.MaxValue)%receiveLocks.Length];targetLock.Wait(cancel);
            CancellationTokenRegistration activeCancellation=cancel.Register(()=>{
                TcpClient active=connection??(pd==null?null:pd.DirectClient??pd.PushedClient);
                if(active!=null)try{active.Close();}catch(ObjectDisposedException){}
            });
            try {
                if(!rename&&(File.Exists(path)||Directory.Exists(path)))throw new IOException("目标已存在，不会覆盖。");
                cancel.ThrowIfCancellationRequested();
                using(FileStream output=new FileStream(part,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                    owned=true;if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("断点文件不能是链接。");
                    if(output.Length>file.Size)output.SetLength(0);long offset=output.Length;output.Position=offset;
                    channel=TakeFileConnection(peer);
                    borrowedConnection=channel!=null;
                    int receiveEpoch=TransportEpoch;
                    string[] response;
                    while(true) {
                        cancel.ThrowIfCancellationRequested();
                        if(receiveEpoch!=TransportEpoch)throw new IOException("网络已变化，请重新接收。");
                        pd=new PendingDownload(peer.DeviceId,peer.Fingerprint,packet,file.Id,offset);
                        PendingDownload activePd=pd;
                        if(channel!=null) {
                            connection=channel.Client;directStream=channel.Stream;pd.DirectClient=connection;pd.Claimed=1;
                        } else {
                            lock(gate)pendingDownloads.Add(pd);
                            Task.Delay(150).ContinueWith(_=>{
                                if(activePd.Claimed==0&&!disposed)
                                    try{Send(Encode(Protocol.Entry|Protocol.CapUtf8|Protocol.FileAttach,Protocol.Field(Settings.Nickname),Protocol.Field(Settings.Group)),peer.Endpoint);}catch{}
                            },TaskScheduler.Default);
                        }
                        TimeSpan waitUntil=clock.Elapsed+TimeSpan.FromSeconds(8);
                        Exception connectError=null;
                        if(channel==null)try {
                            connection=SecureConnect(peer.Endpoint,c=>{activePd.DirectClient=c;RegisterConnectionFingerprint(c,peer.Fingerprint);if(cancel.IsCancellationRequested)c.Close();});
                            directStream=Security.Client(connection,peer.Fingerprint);
                            Packet remote=AuthenticateSecureClient(directStream,peer.DeviceId,peer.Fingerprint,peer.DisplayName);
                            Upsert(remote,peer.Endpoint);
                            if(Interlocked.CompareExchange(ref pd.Claimed,1,0)==0) {
                                lock(gate)pendingDownloads.Remove(pd);
                                if(remote.BatchFiles)channel=CreateFileConnection(connection,directStream,peer);
                            } else {
                                try{directStream.Dispose();}catch{}directStream=null;
                                try{connection.Close();}catch{}ReleaseConnection(connection);connection=null;
                            }
                        } catch(Exception e) {
                            connectError=e is AggregateException?e.GetBaseException():e;
                            if(directStream!=null){try{directStream.Dispose();}catch{}directStream=null;}
                            if(connection!=null){try{connection.Close();}catch{}ReleaseConnection(connection);connection=null;}
                        }
                        if(directStream==null) {
                            pd.DirectFailed=true;
                            cancel.ThrowIfCancellationRequested();
                            string msg=connectError==null?null:connectError.Message;
                            if(connectError is SecureIdentity.TrustException||connectError is System.Security.Authentication.AuthenticationException||msg=="服务已关闭或连接任务过多。"
                                ||msg=="回环模式禁止连接局域网。"||msg=="目标不在当前物理局域网内。")throw connectError;
                            while(pd.PushedStream==null&&HeartbeatRetry>=TimeSpan.FromSeconds(1)&&clock.Elapsed<waitUntil) {
                                cancel.ThrowIfCancellationRequested();
                                if(disposed)throw new ObjectDisposedException("LanService");
                                if(pd.Ready.Wait(100,cancel))break;
                            }
                            if(pd.PushedStream==null)throw connectError??new IOException("加密连接超时。");
                        }
                        if(directStream==null)response=SecureIdentity.ReadText(pd.PushedStream,256).Split('\n');
                        else {
                            byte[] request=Protocol.Encode(Next(),Login,Host,Protocol.GetFile|Protocol.Utf8,
                                packet.ToString("x")+":"+file.Id.ToString("x")+":"+offset.ToString("x")+":",null,Settings.DeviceId,null,Security.Fingerprint);
                            cancel.ThrowIfCancellationRequested();
                            try {
                                SecureIdentity.WriteText(directStream,"F\n"+Port);SecureIdentity.WriteFrame(directStream,request);
                                response=SecureIdentity.ReadText(directStream,256).Split('\n');break;
                            } catch(IOException error) {
                                if(!borrowedConnection||cancel.IsCancellationRequested||channel.Epoch!=TransportEpoch||!Security.IsTrusted(peer.DeviceId,peer.Fingerprint)
                                    ||!(error is EndOfStreamException||error.GetBaseException() is SocketException))throw;
                                // 回到完整建连路径，重新登记反向 P，单向可达时也能完成这一次重试。
                                borrowedConnection=false;CloseFileConnection(channel);channel=null;
                                directStream=null;connection=null;
                                lock(gate)pendingDownloads.Remove(pd);
                                pd.Done.Set();
                                continue;
                            }
                        }
                        break;
                    }
                    SslStream stream=directStream??pd.PushedStream;
                    {
                        if (response.Length == 1 && response[0] == "REVOKED") { revoked = true; throw new OfferRevokedException(); }
                        if(response.Length!=3||response[0]!="OK"||response[1]!=file.Size.ToString(CultureInfo.InvariantCulture)||response[2]!=file.Sha256)throw new IOException("发送方文件信息已变化。");
                        int initialPercent=(int)(offset*100.0/Math.Max(1,file.Size));
                        if(progress!=null)progress.Report(initialPercent);
                        if(details!=null)details.Report(new TransferProgress(initialPercent,0));
                        byte[] buffer=new byte[65536];long received=offset;TransferRate rate=new TransferRate(offset);var timer=System.Diagnostics.Stopwatch.StartNew();
                        while(received<file.Size){
                            cancel.ThrowIfCancellationRequested();
                            int count=stream.Read(buffer,0,(int)Math.Min(buffer.Length,file.Size-received));
                            if(count==0)throw new IOException("传输中断，已保留断点。");
                            output.Write(buffer,0,count);
                            received+=count;
                            bool sampled=rate.Update(received,timer.Elapsed.TotalMilliseconds);
                            if(sampled){
                                int percent=(int)(received*100.0/Math.Max(1,file.Size));
                                if(progress!=null)progress.Report(percent);
                                if(details!=null)details.Report(new TransferProgress(percent,rate.BytesPerSecond));
                            }
                        }
                        output.Flush(true);output.Position=0;if(details!=null)details.Report(new TransferProgress(100,0,"正在校验"));
                        string hash;using(SHA256 sha=SHA256.Create()) {
                            int count;while((count=output.Read(buffer,0,buffer.Length))>0){cancel.ThrowIfCancellationRequested();sha.TransformBlock(buffer,0,count,buffer,0);}
                            sha.TransformFinalBlock(buffer,0,0);hash=BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
                        }
                        if(hash!=file.Sha256){output.SetLength(0);throw new IOException("文件校验失败，已清除错误断点，请重新接收。");}
                    }
                }
                cancel.ThrowIfCancellationRequested();string original=path;int suffix=0;
                while(true){
                    try{
                        File.Move(part,path);
                        break;
                    }
                    catch(IOException){
                        if(!rename||(!File.Exists(path)&&!Directory.Exists(path))||++suffix>10000)throw;
                        path=Path.Combine(Path.GetDirectoryName(original),Path.GetFileNameWithoutExtension(original)+" ("+suffix+")"+Path.GetExtension(original));
                    }
                }
                SslStream finalStream=directStream??pd.PushedStream;
                // 正式文件移动成功后，本地保存不可被远端确认失败改写；异常连接不再复用。
                try {
                    SecureIdentity.WriteText(finalStream,"STORED\n"+file.Sha256);
                    if(channel!=null) {
                        string boundary=SecureIdentity.ReadText(finalStream,16);
                        if(boundary!="NEXT"&&boundary!="DONE")throw new IOException("批量文件边界确认无效，连接已丢弃。");
                        channel.Files++;reuseConnection=boundary=="NEXT";
                    }
                }
                catch(IOException){Report("文件已校验保存，远端确认未完成；连接已丢弃。");}
                catch(ObjectDisposedException){Report("文件已校验保存，远端确认未完成；连接已丢弃。");}
                if(progress!=null)progress.Report(100);return path;
            } catch(Exception){if(cancel.IsCancellationRequested)throw new OperationCanceledException(cancel);throw;}
            finally {
                activeCancellation.Dispose();
                if(pd!=null){lock(gate)pendingDownloads.Remove(pd);pd.Done.Set();}
                if(channel!=null) {
                    if(reuseConnection)ReturnFileConnection(channel,cancel);else CloseFileConnection(channel);
                    directStream=null;connection=null;
                }
                if(directStream!=null)try{directStream.Dispose();}catch{}
                if(connection!=null){try{connection.Close();}catch{}ReleaseConnection(connection);}
                try{
                    if(owned&&File.Exists(part)&&(revoked||cancel.IsCancellationRequested&&!(keepPartial!=null&&keepPartial())
                        ||new FileInfo(part).Length==0))File.Delete(part);
                }
                finally{
                    targetLock.Release();
                }
            }
        }
    }
}
