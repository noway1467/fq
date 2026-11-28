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
        private void CancelReverseRequests(){CloseFileBatches(null);FlushOfferCompletions();transportEpoch++;foreach(ReverseRequest request in reverseRequests.Values)request.Ready.Set();reverseRequests.Clear();}

        private void Send(byte[] bytes, IPEndPoint endpoint)
        {
            if (disposed) throw new ObjectDisposedException("LanService");
            if (LoopbackOnly && !IPAddress.IsLoopback(endpoint.Address)) throw new InvalidOperationException("回环测试模式不能连接局域网。");
            lock(sendGate) {
                if(!LoopbackOnly) {
                    LanNetwork.Link route=LanNetwork.Route(links,endpoint.Address);
                    if(route==null)throw new IOException("目标不在当前物理局域网内，请重新发现设备。");
                    // 只约束本应用的发送接口，VPN 开启时仍优先使用物理局域网。
                    udp.Client.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,IPAddress.HostToNetworkOrder(route.Index));
                }
                udp.Send(bytes, bytes.Length, endpoint);
            }
        }

        private void AcceptLoop()
        {
            while (!disposed)
            {
                try
                {
                    TcpClient client = tcp.AcceptTcpClient();
                    lock (gate) { if (connections.Count >= 8 || disposed) { client.Close(); continue; } connections.Add(client); }
                    Task.Run(() => DispatchConnection(client));
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException e) { if (!disposed) Report("文件服务错误：" + e.Message); }
            }
        }

        private void DispatchConnection(TcpClient client)
        {
            bool handedOff=false;
            try {
                if(!LoopbackOnly&&LanNetwork.Route(links,((IPEndPoint)client.Client.RemoteEndPoint).Address)==null)return;
                RequestInput prefix=new RequestInput(client);prefix.Begin(5000);
                if(prefix.ReadByte()!='F'||prefix.ReadByte()!='Q')return;
                int operation=prefix.ReadByte();if(prefix.ReadByte()!='\n')return;
                if(operation=='D')ServeDiscovery(client,prefix);
                else if(operation=='2'&&Security!=null)ServeSecure(client);
                else if(operation=='R'&&Security!=null)handedOff=AcceptReverse(client,prefix);
            }
            catch(IOException e) {
                // 切网、对端取消及回连抢占会关闭单条连接，不代表监听或联系人已离线。
                // 文件中断仍由传输任务反馈；协议、身份和本地 IO 错误继续向用户报告。
                if(e is EndOfStreamException || e.GetBaseException() is SocketException)ReportConnectionInterrupted();
                else Report("安全连接未完成："+e.Message);
            }
            catch(SocketException) {ReportConnectionInterrupted();}
            catch(System.Security.Authentication.AuthenticationException e) {
                if(e.GetBaseException() is SocketException)ReportConnectionInterrupted();
                else Report("TLS 握手未通过："+e.GetBaseException().Message);
            }
            catch(ObjectDisposedException) {}
            catch(System.ComponentModel.Win32Exception e){Report("TLS 系统凭据失败："+e.NativeErrorCode+" "+e.Message);}
            catch(CryptographicException e){Report("TLS 证书失败："+e.Message);}
            finally {if(!handedOff){client.Close();lock(gate)connections.Remove(client);}}
        }
        private bool AcceptReverse(TcpClient client,Stream input)
        {
            string ticket=System.Text.Encoding.ASCII.GetString(SecureIdentity.ReadBytes(input,32));
            lock(gate){ReverseRequest request;if(!reverseRequests.TryGetValue(ticket,out request)||!Security.IsTrusted(request.DeviceId,request.Fingerprint))return false;
                reverseRequests.Remove(ticket);request.Client=client;RegisterConnectionFingerprint(client,request.Fingerprint);request.Direct.Close();request.Ready.Set();return true;}
        }
        // 回连只改变 TCP 建连方向，调用方仍执行原有 TLS、设备配对和业务权限检查。
        internal TcpClient ConnectPeer(Peer peer,int port,CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            TcpClient direct=new TcpClient(AddressFamily.InterNetwork);ReverseRequest request=null;string ticket=Guid.NewGuid().ToString("N");
            lock(gate){if(disposed||connections.Count>=8){direct.Close();throw new IOException("服务已关闭或连接任务过多。");}connections.Add(direct);
                RegisterConnectionFingerprint(direct,peer.Fingerprint);
                if(Security.IsTrusted(peer.DeviceId,peer.Fingerprint)){
                    request=new ReverseRequest{
                        DeviceId=peer.DeviceId,Fingerprint=peer.Fingerprint,Port=port,Direct=direct
                    };
                    reverseRequests.Add(ticket,request);
                }
                }
            CancellationTokenRegistration registration=cancellation.Register(()=>{
                lock(gate)
                {
                    reverseRequests.Remove(ticket);direct.Close();connections.Remove(direct);
                    if(request!=null){if(request.Client!=null){request.Client.Close();connections.Remove(request.Client);}request.Ready.Set();}
                }
            });
            try {
                cancellation.ThrowIfCancellationRequested();
                if(LoopbackOnly&&!IPAddress.IsLoopback(peer.Endpoint.Address))throw new IOException("回环模式禁止连接局域网。");
                BindClient(direct,peer.Endpoint.Address);
                if(request!=null)Task.Delay(150).ContinueWith(_=>{
                    lock(gate)if(cancellation.IsCancellationRequested||!reverseRequests.ContainsKey(ticket))return;
                    try{
                        Probe(peer.Endpoint);
                    }
                    catch(IOException){
                    }
                    catch(SocketException){
                    }
                    catch(ObjectDisposedException){
                    }
                },TaskScheduler.Default);
                Exception failure=null;
                try{PeerConnector(direct,new IPEndPoint(peer.Endpoint.Address,port));}
                catch(Exception e){if(!(e is SocketException||e is IOException||e is AggregateException||e is ObjectDisposedException))throw;failure=e;}
                cancellation.ThrowIfCancellationRequested();
                if(failure==null){lock(gate){cancellation.ThrowIfCancellationRequested();reverseRequests.Remove(ticket);if(request==null||request.Client==null){direct.ReceiveTimeout=45000;direct.SendTimeout=15000;return direct;}}}
                direct.Close();ReleaseConnection(direct);
                if(request==null)throw new IOException("无法连接对方。",failure);
                if(!request.Ready.Wait(8000))throw new IOException("无法连接对方，请确认双方已升级并允许局域网连接。",failure);
                cancellation.ThrowIfCancellationRequested();
                lock(gate){if(request.Client==null||disposed||!Security.IsTrusted(request.DeviceId,request.Fingerprint))throw new IOException("连接已取消或设备信任已撤销。");}
                request.Client.ReceiveTimeout=45000;request.Client.SendTimeout=15000;return request.Client;
            }catch{direct.Close();ReleaseConnection(direct);lock(gate)if(request!=null&&request.Client!=null){request.Client.Close();connections.Remove(request.Client);}throw;}
            finally{registration.Dispose();lock(gate)reverseRequests.Remove(ticket);}
        }
        internal void ReleaseConnection(TcpClient client){lock(gate){connections.Remove(client);connectionFingerprints.Remove(client);}}
        private void ServeReverse(IPEndPoint endpoint,int servicePort,string ticket,string id,string fingerprint,int epoch)
        {
            int sharePort=Port==65535?65534:Port+1,phonePort=Port>=65534?Port-2:Port+2;
            if(servicePort!=Port&&servicePort!=sharePort&&servicePort!=phonePort)return;
            TcpClient client=new TcpClient(AddressFamily.InterNetwork);
            lock(gate){if(disposed||epoch!=transportEpoch||connections.Count>=8||!Security.IsTrusted(id,fingerprint)){client.Close();return;}connections.Add(client);}
            RegisterConnectionFingerprint(client,fingerprint);
            try {
                BindClient(client,endpoint.Address);if(!client.ConnectAsync(endpoint.Address,endpoint.Port).Wait(8000))throw new IOException("回连超时。");
                client.ReceiveTimeout=5000;client.SendTimeout=15000;
                byte[] prefix=System.Text.Encoding.ASCII.GetBytes("FQR\n"+ticket);client.GetStream().Write(prefix,0,prefix.Length);
                if(servicePort==Port)DispatchConnection(client);else Security.AcceptReverse(servicePort,client);
            }catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}catch(AggregateException){}
            finally{client.Close();ReleaseConnection(client);}
        }

        private TcpClient SecureConnect(IPEndPoint endpoint, Action<TcpClient> beforeConnect = null)
        {
            if(LoopbackOnly&&!IPAddress.IsLoopback(endpoint.Address))throw new IOException("回环模式禁止连接局域网。");
            TcpClient client=new TcpClient(AddressFamily.InterNetwork);
            lock(gate){if(disposed||connections.Count>=8){client.Close();throw new IOException("服务已关闭或连接任务过多。");}connections.Add(client);}
            try {
                if(beforeConnect!=null)beforeConnect(client);
                BindClient(client,endpoint.Address);
                Task connecting=client.ConnectAsync(endpoint.Address,endpoint.Port);
                connecting.ContinueWith(t => { var unused = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                if(!connecting.Wait(8000))throw new IOException("加密连接超时。");
                client.ReceiveTimeout=5000;client.SendTimeout=15000;client.GetStream().Write(new byte[]{70,81,50,10},0,4);return client;
            }
            catch{client.Close();lock(gate)connections.Remove(client);throw;}
        }
        private void BindClient(TcpClient client,IPAddress destination)
        {
            if(!LoopbackOnly){
                LanNetwork.Link route=LanNetwork.Route(links,destination);
                if(route==null)throw new IOException("目标不在当前物理局域网内。");
                client.Client.Bind(new IPEndPoint(route.Address,0));
                try{
                    client.Client.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,IPAddress.HostToNetworkOrder(route.Index));
                }
                catch(SocketException){
                }
            }
        }
    }
}
