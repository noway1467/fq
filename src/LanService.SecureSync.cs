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
        private readonly Dictionary<TcpClient,string> connectionFingerprints=new Dictionary<TcpClient,string>();
        internal void RegisterConnectionFingerprint(TcpClient client,string fingerprint)
        {
            lock(gate)
            {
                foreach(TcpClient stale in connectionFingerprints.Keys.Where(value=>!connections.Contains(value)&&!probeSockets.Contains(value)).ToArray())
                    connectionFingerprints.Remove(stale);
                connectionFingerprints[client]=fingerprint;
            }
        }
        public void ConfigureSecurity(string root, Func<string,string,bool> confirmation)
        {
            if(udp!=null)throw new InvalidOperationException("安全身份必须在启动前配置。");
            dataRoot=root;Security=new SecureIdentity(root);Security.ConfirmPeer=confirmation;Security.TrustRevoked+=OnTransportRevoked;RestoreOffers();
        }
        private void OnTransportRevoked(string fingerprint)
        {
            lock(gate)
            {
                foreach(TcpClient client in connectionFingerprints.Where(item=>item.Value==fingerprint).Select(item=>item.Key).ToArray())
                {
                    client.Close();
                    connections.Remove(client);
                    probeSockets.Remove(client);
                    connectionFingerprints.Remove(client);
                }
                foreach(string ticket in reverseRequests.Where(item=>item.Value.Fingerprint==fingerprint).Select(item=>item.Key).ToArray())
                {
                    ReverseRequest request=reverseRequests[ticket];
                    reverseRequests.Remove(ticket);
                    request.Direct.Close();
                    connections.Remove(request.Direct);
                    if(request.Client!=null){request.Client.Close();connections.Remove(request.Client);}
                    request.Ready.Set();
                }
                foreach(PendingDownload download in pendingDownloads.Where(item=>item.Fingerprint==fingerprint))
                {
                    if(download.DirectClient!=null)download.DirectClient.Close();
                    if(download.PushedClient!=null)download.PushedClient.Close();
                    download.Ready.Set();
                }
                foreach(Offer offer in offers.Values.Where(item=>item.Fingerprint==fingerprint))
                    foreach(TcpClient client in offer.ActiveClients)client.Close();
            }
        }

        private bool HasPendingFor(string targetDeviceId, string targetFingerprint)
        {
            if (targetDeviceId == null || targetFingerprint == null) return false;
            lock (gate)
            {
                return pending.Values.Any(item => !item.Delivered && !item.Canceled && item.DeviceId == targetDeviceId && item.Fingerprint == targetFingerprint)
                    || pendingDownloads.Any(pd => pd.Claimed == 0 && pd.DeviceId == targetDeviceId && pd.Fingerprint == targetFingerprint);
            }
        }
        private void ScheduleSync(Peer peer)
        {
            if (disposed || tcp == null || Security == null || peer == null || peer.DeviceId == null || peer.Fingerprint == null) return;
            string key = peer.DeviceId;
            lock (gate) { if (!activeSyncPeers.Add(key)) return; }
            Task.Run(() =>
            {
                probeConcurrency.Wait();
                try { SyncPeer(peer); }
                finally { probeConcurrency.Release(); lock (gate) activeSyncPeers.Remove(key); }
            });
        }
        private void SyncPeer(Peer peer)
        {
            if (disposed || tcp == null || Security == null || peer.DeviceId == null || peer.Fingerprint == null) return;
            IPEndPoint target = peer.Endpoint;
            if (LoopbackOnly && !IPAddress.IsLoopback(target.Address)) return;
            if (!LoopbackOnly && LanNetwork.Route(links, target.Address) == null) return;
            TcpClient client = new TcpClient(AddressFamily.InterNetwork);
            lock (gate) { if (disposed) { client.Close(); return; } probeSockets.Add(client); }
            RegisterConnectionFingerprint(client,peer.Fingerprint);
            try
            {
                using (client)
                {
                    BindClient(client, target.Address);
                    Task connecting = client.ConnectAsync(target.Address, target.Port);
                    connecting.ContinueWith(t => { var unused = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    if (!connecting.Wait(2500)) return;
                    client.ReceiveTimeout = 15000; client.SendTimeout = 15000;
                    client.GetStream().Write(new byte[] { 70, 81, 50, 10 }, 0, 4);
                    using (SslStream stream = Security.Client(client, peer.Fingerprint))
                    {
                        stream.ReadTimeout = 15000; stream.WriteTimeout = 15000;
                        Packet remote = AuthenticateSecureClient(stream, peer.DeviceId, peer.Fingerprint, peer.DisplayName, true);
                        SecureIdentity.WriteText(stream, "S\n" + Port);
                        SecureIdentity.WriteFrame(stream, SecureSyncHello());
                        Upsert(remote, target);
                        while (!disposed)
                        {
                            string cmd = SecureIdentity.ReadText(stream, 1024);
                            if (cmd == "OK") break;
                            if (cmd == "M")
                            {
                                Packet msg;
                                if (!Protocol.TryParse(SecureIdentity.ReadFrame(stream, 60000), out msg) || msg.DeviceId != peer.DeviceId
                                    || msg.Fingerprint != peer.Fingerprint || (msg.Mode != Protocol.SendMessage && msg.Mode != Protocol.ReleaseFiles))
                                    throw new IOException("同步消息无效。");
                                Handle(msg, target, true);
                                SecureIdentity.WriteText(stream, "OK");
                            }
                            else if (cmd.StartsWith("D\n", StringComparison.Ordinal))
                            {
                                string[] f = cmd.Substring(2).Split(':');
                                long number, offset; int id;
                                if (f.Length >= 3 && Int64.TryParse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number)
                                    && Int32.TryParse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
                                    && Int64.TryParse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset))
                                    Task.Run(() => PushSecureFile(peer, number, id, offset));
                            }
                            else if(cmd.StartsWith("R\n",StringComparison.Ordinal))
                            {
                                string[] fields=cmd.Substring(2).Split(':');int port;
                                if(fields.Length!=2||Protocol.NormalizeDeviceId(fields[0])==null||!Int32.TryParse(fields[1],out port))throw new IOException("回连请求无效。");
                                string ticket=fields[0];string key=peer.DeviceId+":"+ticket;int epoch;
                                lock(gate){if(activeReverseRequests.Count>=8||!activeReverseRequests.Add(key))continue;epoch=transportEpoch;}
                                Task.Run(()=>{try{ServeReverse(target,port,ticket,peer.DeviceId,peer.Fingerprint,epoch);}finally{lock(gate)activeReverseRequests.Remove(key);}});
                            }
                            else throw new IOException("未知同步响应。");
                        }
                    }
                }
            }
            catch (Exception) { }
            finally { lock (gate) {probeSockets.Remove(client);connectionFingerprints.Remove(client);} }
        }
        private void PushSecureFile(Peer peer, long number, int fileId, long offset)
        {
            Offer offer;
            lock (gate) { if (!offers.TryGetValue(number + ":" + fileId, out offer) || offer.Canceled) return; }
            if (offer.DeviceId != peer.DeviceId || offer.Fingerprint != peer.Fingerprint || offer.Expires < DateTime.UtcNow || offset < 0 || offset > offer.Size) return;
            if (Interlocked.CompareExchange(ref offer.Pushing, 1, 0) != 0) return;
            TcpClient connection = null;
            try
            {
                using (TcpClient client = connection = SecureConnect(peer.Endpoint,c=>RegisterConnectionFingerprint(c,peer.Fingerprint)))
                using (SslStream stream = Security.Client(client, peer.Fingerprint))
                {
                    client.ReceiveTimeout = 45000; stream.ReadTimeout = 45000;
                    Packet remote = AuthenticateSecureClient(stream, peer.DeviceId, peer.Fingerprint, peer.DisplayName);
                    Upsert(remote, peer.Endpoint);
                    SecureIdentity.WriteText(stream, "P\n" + Port);
                    SecureIdentity.WriteFrame(stream, Protocol.Encode(Next(), Login, Host, Protocol.GetFile | Protocol.Utf8,
                        number.ToString("x") + ":" + fileId.ToString("x") + ":" + offset.ToString("x") + ":", null, Settings.DeviceId, null, Security.Fingerprint));
                    StreamOffer(client,stream,number+":"+fileId,o=>o.DeviceId==peer.DeviceId&&o.Fingerprint==peer.Fingerprint&&offset>=0&&offset<=o.Size,offset);
                }
            }
            catch (Exception) { }
            finally
            {
                Interlocked.Exchange(ref offer.Pushing, 0);
                if (connection != null) lock (gate) connections.Remove(connection);
            }
        }

        private void SendSecurePending(Pending item)
        {
            if(item.Delivered||item.Canceled||Interlocked.CompareExchange(ref item.Sending,1,0)!=0)return;
            item.AttemptStarted=clock.Elapsed;
            item.DirectFailed=false;
            TcpClient connection=null;
            Task.Delay(150).ContinueWith(_ =>
            {
                if(!item.Delivered&&!item.Canceled&&item.ConnectingClient!=null&&!disposed)
                    try{Send(Encode(Protocol.Entry|Protocol.CapUtf8|Protocol.FileAttach,Protocol.Field(Settings.Nickname),Protocol.Field(Settings.Group)),item.Endpoint);}catch{}
            }, TaskScheduler.Default);
            try {
                using(TcpClient client=connection=SecureConnect(item.Endpoint,c=>{
                    RegisterConnectionFingerprint(c,item.Fingerprint);
                    lock(gate){
                        if(item.Canceled||item.Delivered){
                            c.Close();
                            throw new IOException("已取消发送。");
                        }
                        item.ConnectingClient=c;
                    }
                }))using(SslStream stream=Security.Client(client,item.Fingerprint)) {
                    if(item.Delivered||item.Canceled)return;
                    client.ReceiveTimeout=45000;stream.ReadTimeout=45000;
                    Packet remote=AuthenticateSecureClient(stream,item.DeviceId,item.Fingerprint,item.Login);
                    if(item.Delivered||item.Canceled||Interlocked.CompareExchange(ref item.Transmitting,1,0)!=0)return;
                    try {
                        if(item.Delivered||item.Canceled)return;
                        SaveOffers();
                        lock(gate){Pending active;if(item.Canceled||item.Delivered||!pending.TryGetValue(item.Record.Packet,out active)||active!=item)return;}
                        SecureIdentity.WriteText(stream,"M\n"+Port);SecureIdentity.WriteFrame(stream,item.Bytes);
                        if(SecureIdentity.ReadText(stream,1024)!="OK")throw new IOException("对方未接受消息或未完成配对。");
                        lock(gate){
                            Pending active;
                            if(item.Canceled||!pending.TryGetValue(item.Record.Packet,out active)||active!=item)return;
                            item.Delivered=true;
                            item.Record.State="已送达";
                            pending.Remove(item.Record.Packet);
                        }
                        Upsert(remote,item.Endpoint);
                        Complete(item.Record,"已送达");
                        if(item.Record.HasAttachments)try{SaveOffers();}catch(IOException){}
                        if(item.Record.HasAttachments&&!disposed&&item.Peer!=null) {
                            Task.Delay(300).ContinueWith(_=>ScheduleSync(item.Peer),TaskScheduler.Default);
                            Task.Delay(1200).ContinueWith(_=>ScheduleSync(item.Peer),TaskScheduler.Default);
                        }
                    } finally {
                        Interlocked.Exchange(ref item.Transmitting,0);
                    }
                }
            } catch(Exception error) {
                item.DirectFailed=true;
                if(item.Delivered||item.Canceled)return;
                if(!(error is IOException||error is SocketException||error is System.Security.Authentication.AuthenticationException||error is AggregateException
                    ||error is ObjectDisposedException))throw;
                Exception baseErr=error.GetBaseException();
                if(baseErr is SecureIdentity.TrustException || baseErr is System.Security.Authentication.AuthenticationException)
                {
                    lock(gate)
                    {
                        Pending active;
                        if(item.Canceled||item.Delivered||!pending.TryGetValue(item.Record.Packet,out active)||active!=item)return;
                        pending.Remove(item.Record.Packet);item.Record.State="身份核对未通过，未确认送达";
                    }
                    NotifyDelivery(item.Record);
                }
                else if(HeartbeatRetry>=TimeSpan.FromSeconds(1)&&!disposed) {
                    TimeSpan waitUntil=clock.Elapsed+TimeSpan.FromMilliseconds(2200);
                    while(!item.Delivered&&!item.Canceled&&!disposed&&clock.Elapsed<waitUntil)Thread.Sleep(50);
                    if(item.Delivered||item.Canceled)return;
                }
                if(!disposed&&!item.Delivered&&!item.Canceled)Report("加密发送未完成："+baseErr.Message);
            }
            finally {lock(gate)item.ConnectingClient=null;if(connection!=null)lock(gate)connections.Remove(connection);item.Last=DateTime.UtcNow;Interlocked.Exchange(ref item.Sending,0);}
        }

        private void ServeSecure(TcpClient client)
        {
            SslStream stream;
            try { stream=Security.Server(client,true); }
            // SslStream 在握手中收到 EOF 时可抛出没有内部 SocketException 的 IOException。
            // 仅在握手的网络 IO 阶段归类为中断；后续身份校验和协议错误仍正常报告。
            catch(IOException) { ReportConnectionInterrupted(); return; }
            using(stream) {
                RegisterConnectionFingerprint(client,SecureIdentity.RemoteFingerprint(stream));
                client.ReceiveTimeout=45000;stream.ReadTimeout=45000;
                Packet identity=AuthenticateSecureServer(stream);
                if(identity==null)return;
                RequestInput input=new RequestInput(client,stream);input.Begin(5000);
                string[] header=SecureIdentity.ReadText(input,128).Split('\n');int senderPort;
                if(header.Length!=2||!Int32.TryParse(header[1],out senderPort)||senderPort<1024||senderPort>65535)throw new IOException("安全报文端口无效。");
                Packet packet;if(!Protocol.TryParse(SecureIdentity.ReadFrame(input,60000),out packet))throw new IOException("安全报文无效。");
                string fingerprint=SecureIdentity.RemoteFingerprint(stream);
                if(packet.DeviceId!=identity.DeviceId||packet.Fingerprint!=fingerprint||packet.Login!=identity.Login)throw new IOException("报文身份与证书不一致。");
                client.ReceiveTimeout=45000;stream.ReadTimeout=45000;
                IPEndPoint endpoint=new IPEndPoint(((IPEndPoint)client.Client.RemoteEndPoint).Address,senderPort);
                if(header[0]=="M"&&(packet.Mode==Protocol.SendMessage||packet.Mode==Protocol.ReleaseFiles)) {Handle(packet,endpoint,true);SecureIdentity.WriteText(stream,"OK");return;}
                if(header[0]=="V"&&packet.Mode==Protocol.Entry){Upsert(packet,endpoint);SecureIdentity.WriteText(stream,"OK");return;}
                if(header[0]=="S"&&(packet.Mode==Protocol.Entry||packet.Mode==Protocol.Absence))
                {
                        Upsert(packet,endpoint);
                        double waitMs=0;
                        lock(gate) {
                            TimeSpan now=clock.Elapsed;
                            foreach(Pending item in pending.Values)if(!item.Delivered&&!item.Canceled&&item.DeviceId==packet.DeviceId&&item.Fingerprint==fingerprint
                                &&!item.DirectFailed){
                                double rem=120-(now-item.AttemptStarted).TotalMilliseconds;
                                if(rem>waitMs)waitMs=rem;
                            }
                            foreach(PendingDownload pd in pendingDownloads)if(pd.Claimed==0&&pd.DeviceId==packet.DeviceId&&pd.Fingerprint==fingerprint&&!pd.DirectFailed){
                                double rem=120-(now-pd.CreatedAt).TotalMilliseconds;
                                if(rem>waitMs)waitMs=rem;
                            }
                        }
                        if(waitMs>0)Thread.Sleep((int)waitMs+5);
                        List<Pending> due=new List<Pending>();
                        lock(gate) {
                            foreach(Pending item in pending.Values.OrderBy(p=>p.Record.Packet))
                                if(!item.Delivered&&!item.Canceled&&item.DeviceId==packet.DeviceId&&item.Fingerprint==fingerprint)due.Add(item);
                        }
                        foreach(Pending item in due) {
                            // 逐条占用发送权；前一条失败不能把后续消息永久留在“发送中”。
                            if(Interlocked.CompareExchange(ref item.Transmitting,1,0)!=0)continue;
                            try {
                                lock(gate){
                                    Pending current;
                                    if(item.Delivered||item.Canceled||!pending.TryGetValue(item.Record.Packet,out current)||current!=item
                                        ||!Security.IsTrusted(item.DeviceId,item.Fingerprint))continue;
                                }
                                SaveOffers();
                                lock(gate){Pending current;if(item.Delivered||item.Canceled||!pending.TryGetValue(item.Record.Packet,out current)||current!=item)continue;}
                                SecureIdentity.WriteText(stream,"M");
                                SecureIdentity.WriteFrame(stream,item.Bytes);
                                if(SecureIdentity.ReadText(stream,1024)=="OK") {
                                    TcpClient connecting;bool delivered=false;
                                    lock(gate){
                                        Pending active;
                                        if(!item.Canceled&&pending.TryGetValue(item.Record.Packet,out active)&&active==item){
                                            item.Delivered=true;
                                            item.Record.State="已送达";
                                            connecting=item.ConnectingClient;
                                            item.ConnectingClient=null;
                                            pending.Remove(item.Record.Packet);
                                            delivered=true;
                                        }
                                        else connecting=null;
                                    }
                                    if(connecting!=null)try{connecting.Close();}catch{}
                                    if(!delivered)continue;
                                    Complete(item.Record,"已送达");
                                    if(item.Record.HasAttachments)try{SaveOffers();}catch(IOException){}
                                    if(item.Record.HasAttachments&&!disposed&&item.Peer!=null) {
                                        Task.Delay(300).ContinueWith(_=>ScheduleSync(item.Peer),TaskScheduler.Default);
                                        Task.Delay(1200).ContinueWith(_=>ScheduleSync(item.Peer),TaskScheduler.Default);
                                    }
                                }
                            } finally {
                                item.Last=DateTime.UtcNow;
                                Interlocked.Exchange(ref item.Transmitting,0);
                            }
                        }
                        List<PendingDownload> pulls=new List<PendingDownload>();
                        lock(gate) {
                            foreach(PendingDownload pd in pendingDownloads)if(pd.Claimed==0&&pd.DeviceId==packet.DeviceId&&pd.Fingerprint==fingerprint)pulls.Add(pd);
                        }
                        foreach(PendingDownload pd in pulls) {
                            SecureIdentity.WriteText(stream,"D\n"+pd.Packet.ToString("x")+":"+pd.FileId.ToString("x")+":"+pd.Offset.ToString("x"));
                        }
                        if(packet.ReverseConnections){
                            List<KeyValuePair<string,ReverseRequest>> requests;
                            lock(gate)requests=reverseRequests.Where(p=>p.Value.DeviceId==packet.DeviceId&&p.Value.Fingerprint==fingerprint).ToList();
                            foreach(var request in requests)SecureIdentity.WriteText(stream,"R\n"+request.Key+":"+request.Value.Port);}
                        SecureIdentity.WriteText(stream,"OK");
                        return;
                }
                if(header[0]=="P"&&packet.Mode==Protocol.GetFile)
                {
                    Upsert(identity,endpoint);
                    string[] pFields=packet.Body.Split(':');long pNum,pOff;int pFileId;
                    if(pFields.Length<3||!Int64.TryParse(pFields[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out pNum)
                        ||!Int32.TryParse(pFields[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out pFileId)
                        ||!Int64.TryParse(pFields[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out pOff))throw new IOException("文件请求无效。");
                    PendingDownload match=null;TcpClient direct=null;
                    lock(gate) {
                        foreach(PendingDownload pd in pendingDownloads) {
                            if(pd.DeviceId==packet.DeviceId&&pd.Fingerprint==fingerprint&&pd.Packet==pNum&&pd.FileId==pFileId&&pd.Offset==pOff&&Interlocked.CompareExchange(ref pd.Claimed,1,0)==0) {
                                match=pd;pendingDownloads.Remove(pd);pd.PushedClient=client;pd.PushedStream=stream;direct=pd.DirectClient;pd.Ready.Set();break;
                            }
                        }
                    }
                    if(match==null)return;
                    if(direct!=null)try{direct.Close();}catch{}
                    while(!match.Done.Wait(1000)&&!disposed&&client.Connected){}
                    return;
                }
                if(header[0]!="F"||packet.Mode!=Protocol.GetFile)throw new IOException("未知安全操作。");
                Upsert(identity,endpoint);
                if(identity.BatchFiles)lock(gate)fileBatchPeers[client]=identity.DeviceId;
                try {
                    int limit=identity.BatchFiles?BatchFileLimit:1;
                    for(int index=0;index<limit;index++) {
                        if(!Security.IsTrusted(identity.DeviceId,fingerprint))throw new IOException("设备信任已撤销。");
                        string[] fields=packet.Body.Split(':');long number,offset;int fileId;
                        if(fields.Length<3||!Int64.TryParse(fields[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out number)||number<0
                            ||!Int32.TryParse(fields[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out fileId)||fileId<0
                            ||!Int64.TryParse(fields[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out offset)||offset<0)throw new IOException("文件请求无效。");
                        client.ReceiveTimeout=45000;stream.ReadTimeout=45000;
                        StreamOffer(client,stream,number+":"+fileId,o=>o.DeviceId==identity.DeviceId&&o.Fingerprint==fingerprint&&offset<=o.Size,offset);
                        if(!identity.BatchFiles)break;
                        SecureIdentity.WriteText(stream,index+1==limit?"DONE":"NEXT");
                        if(index+1==limit)break;
                        input.Begin(BatchRequestMilliseconds);
                        try{header=SecureIdentity.ReadText(input,128).Split('\n');}
                        catch(EndOfStreamException){return;}
                        catch(IOException error){if(error.GetBaseException() is SocketException||error.Message=="请求读取超时")return;throw;}
                        int nextPort;
                        if(header.Length!=2||header[0]!="F"||!Int32.TryParse(header[1],out nextPort)||nextPort!=senderPort
                            ||!Protocol.TryParse(SecureIdentity.ReadFrame(input,60000),out packet)||packet.Mode!=Protocol.GetFile
                            ||packet.DeviceId!=identity.DeviceId||packet.Fingerprint!=fingerprint||packet.Login!=identity.Login)throw new IOException("批量文件报文身份或边界无效。");
                    }
                } finally {lock(gate)fileBatchPeers.Remove(client);if(identity.BatchFiles)FlushOfferCompletions();}
            }
        }
        private byte[] SecureHello() {
            return Protocol.Encode(0,Login,Host,Protocol.Entry|Protocol.Utf8,Protocol.Field(Settings.Nickname),Protocol.Field(Settings.Group),Settings.DeviceId,null,
                Security.Fingerprint).Concat(System.Text.Encoding.ASCII.GetBytes("FQBATCH=1\0")).ToArray();
        }
        private byte[] SecureSyncHello() {
            return Protocol.Encode(0,Login,Host,Protocol.Absence|Protocol.Utf8,Protocol.Field(Settings.Nickname),Protocol.Field(Settings.Group),Settings.DeviceId,null,
                Security.Fingerprint).Concat(System.Text.Encoding.ASCII.GetBytes("FQREV=1\0")).ToArray();
        }
        private TcpClient SecurePeerConnect(Peer peer){
            TcpClient client=ConnectPeer(peer,peer.Endpoint.Port);
            try{
                client.GetStream().Write(new byte[]{
                    70,81,50,10
                },0,4);
                return client;
            }
            catch{
                client.Close();
                ReleaseConnection(client);
                throw;
            }
        }
        private void RequireSecurePeer(string deviceId,string fingerprint){
            if(deviceId==null||fingerprint==null)throw new IOException("对方版本过旧，不支持加密通信，请升级至 0.8 及以上版本。");
            if(Security==null)throw new IOException("本机加密身份未就绪，请重新连接。");
        }
        public void VerifyPeer(Peer peer){
            RequireSecurePeer(peer.DeviceId,peer.Fingerprint);
            using(TcpClient client=SecurePeerConnect(peer))try{
                using(SslStream stream=Security.Client(client,peer.Fingerprint)){
                    Packet remote=AuthenticateSecureClient(stream,peer.DeviceId,peer.Fingerprint,peer.DisplayName);
                    SecureIdentity.WriteText(stream,"V\n"+Port);
                    SecureIdentity.WriteFrame(stream,SecureHello());
                    if(SecureIdentity.ReadText(stream,16)!="OK")throw new IOException("身份验证未完成。");
                    Upsert(remote,peer.Endpoint);
                }
            }
            finally{
                lock(gate)connections.Remove(client);
            }
        }
        public void ForgetSecurity(Peer peer){
            try{
                Security.Forget(peer.DeviceId);
            }
            finally{
                lock(gate){
                    foreach(Pending item in pending.Values.Where(p=>p.DeviceId==peer.DeviceId).ToList()){
                        pending.Remove(item.Record.Packet);
                        Complete(item.Record,"设备信任已撤销，未确认送达");
                    }
                    foreach(string key in offers.Where(o=>o.Value.DeviceId==peer.DeviceId).Select(o=>o.Key).ToList())offers.Remove(key);
                    OnTransportRevoked(peer.Fingerprint);
                    SaveOffers();
                }
            }
        }
        private Packet AuthenticateSecureClient(SslStream stream,string id,string fingerprint,string name,bool sync=false)
        {
            stream.ReadTimeout=45000;stream.WriteTimeout=15000;
            SecureIdentity.WriteFrame(stream,sync?SecureSyncHello():SecureHello());Packet remote;
            if(!Protocol.TryParse(SecureIdentity.ReadFrame(new SecureIdentity.Deadline(stream,5000),Protocol.HelloFrameBytes),out remote)||remote.DeviceId!=id
                ||remote.Fingerprint!=fingerprint)throw new IOException("对方设备身份与所选联系人不一致。");
            bool accepted=Security.EnsureTrusted(id,fingerprint,name);SecureIdentity.WriteText(stream,accepted?"YES":"NO");
            if(!accepted||SecureIdentity.ReadText(new SecureIdentity.Deadline(stream,45000),16)!="YES")throw new SecureIdentity.TrustException("设备配对未通过，请在两端核对后重试。");
            return remote;
        }
        private Packet AuthenticateSecureServer(SslStream stream)
        {
            Packet remote;
            if(!Protocol.TryParse(SecureIdentity.ReadFrame(new SecureIdentity.Deadline(stream,5000),Protocol.HelloFrameBytes),out remote)||remote.DeviceId==null
                ||remote.Fingerprint!=SecureIdentity.RemoteFingerprint(stream))throw new IOException("加密连接身份无效。");
            SecureIdentity.WriteFrame(stream,SecureHello());
            bool accepted=remote.Mode==Protocol.Absence
                ?(Security.IsTrusted(remote.DeviceId,remote.Fingerprint)||(HasPendingFor(remote.DeviceId,remote.Fingerprint)&&Security.EnsureTrusted(remote.DeviceId,remote.Fingerprint,remote.Body)))
                :Security.EnsureTrusted(remote.DeviceId,remote.Fingerprint,remote.Body);
            bool other=SecureIdentity.ReadText(new SecureIdentity.Deadline(stream,45000),16)=="YES";
            SecureIdentity.WriteText(stream,accepted&&other?"YES":"NO");return accepted&&other?remote:null;
        }
    }
}
