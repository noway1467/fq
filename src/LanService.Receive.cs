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
            finally { if (connection != null) lock (gate) connections.Remove(connection); }
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
            TcpClient connection=null;
            SslStream directStream=null;
            PendingDownload pd=null;
            SemaphoreSlim targetLock=receiveLocks[(path.ToUpperInvariant().GetHashCode()&Int32.MaxValue)%receiveLocks.Length];targetLock.Wait(cancel);
            try {
                if(!rename&&(File.Exists(path)||Directory.Exists(path)))throw new IOException("目标已存在，不会覆盖。");
                cancel.ThrowIfCancellationRequested();
                using(FileStream output=new FileStream(part,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                    owned=true;if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("断点文件不能是链接。");
                    if(output.Length>file.Size)output.SetLength(0);long offset=output.Length;output.Position=offset;
                    pd=new PendingDownload(peer.DeviceId,peer.Fingerprint,packet,file.Id,offset);
                    lock(gate)pendingDownloads.Add(pd);
                    PendingDownload activePd=pd;
                    Task.Delay(150).ContinueWith(_=>{
                        if(activePd.Claimed==0&&!disposed)
                            try{Send(Encode(Protocol.Entry|Protocol.CapUtf8|Protocol.FileAttach,Protocol.Field(Settings.Nickname),Protocol.Field(Settings.Group)),peer.Endpoint);}catch{}
                    },TaskScheduler.Default);
                    TimeSpan waitUntil=clock.Elapsed+TimeSpan.FromSeconds(8);
                    Exception connectError=null;
                    try {
                        connection=SecureConnect(peer.Endpoint,c=>activePd.DirectClient=c);
                        directStream=Security.Client(connection,peer.Fingerprint);
                        Packet remote=AuthenticateSecureClient(directStream,peer.DeviceId,peer.Fingerprint,peer.DisplayName);
                        Upsert(remote,peer.Endpoint);
                        if(Interlocked.CompareExchange(ref pd.Claimed,1,0)==0) {
                            lock(gate)pendingDownloads.Remove(pd);
                            byte[] request=Protocol.Encode(Next(),Login,Host,Protocol.GetFile|Protocol.Utf8,
                                packet.ToString("x")+":"+file.Id.ToString("x")+":"+offset.ToString("x")+":",null,Settings.DeviceId,null,Security.Fingerprint);
                            SecureIdentity.WriteText(directStream,"F\n"+Port);SecureIdentity.WriteFrame(directStream,request);
                        } else {
                            try{directStream.Dispose();}catch{}directStream=null;
                            try{connection.Close();}catch{}lock(gate)connections.Remove(connection);connection=null;
                        }
                    } catch(Exception e) {
                        connectError=e is AggregateException?e.GetBaseException():e;
                        if(directStream!=null){try{directStream.Dispose();}catch{}directStream=null;}
                        if(connection!=null){try{connection.Close();}catch{}lock(gate)connections.Remove(connection);connection=null;}
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
                    TcpClient activeClient=connection??pd.PushedClient;
                    SslStream stream=directStream??pd.PushedStream;
                    using(cancel.Register(()=>{try{activeClient.Close();}catch{}})) {
                        string[] response=SecureIdentity.ReadText(stream,256).Split('\n');
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
                try{SecureIdentity.WriteText(finalStream,"STORED\n"+file.Sha256);}catch(IOException){Report("文件已校验保存，但保存确认未送达。");}
                if(progress!=null)progress.Report(100);return path;
            } catch(Exception){if(cancel.IsCancellationRequested)throw new OperationCanceledException(cancel);throw;}
            finally {
                if(pd!=null){lock(gate)pendingDownloads.Remove(pd);pd.Done.Set();}
                if(directStream!=null)try{directStream.Dispose();}catch{}
                if(connection!=null){try{connection.Close();}catch{}lock(gate)connections.Remove(connection);}
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
