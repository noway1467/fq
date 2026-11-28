using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace FeiqLight.Tests
{
    internal sealed class TlsFilePeer : IDisposable
    {
        private readonly SecureIdentity identity;
        private readonly LanService receiver;
        internal readonly Peer Peer;
        internal TlsFilePeer(string root, IPEndPoint endpoint, LanService receiver)
        {
            if (!IPAddress.IsLoopback(endpoint.Address)) throw new ArgumentException("测试仅使用回环地址");
            this.receiver=receiver;
            if(receiver.Security==null)receiver.ConfigureSecurity(Path.Combine(root,"receiver"),(name,code)=>true);
            identity=new SecureIdentity(Path.Combine(root,"source"));
            Peer=new Peer{Endpoint=endpoint,DeviceId=Guid.NewGuid().ToString("N"),Fingerprint=identity.Fingerprint,Login="source",Host="host",Nickname="测试文件源",Utf8=true};
        }
        internal SslStream Open(TcpClient client, Attachment file, out long offset)
        {
            client.ReceiveTimeout=5000;
            if(!SecureIdentity.ReadBytes(client.GetStream(),4).SequenceEqual(new byte[]{70,81,50,10}))throw new IOException("应使用 TLS");
            SslStream stream=identity.Server(client,true);Packet hello,request;
            if(!Protocol.TryParse(SecureIdentity.ReadFrame(stream,4096),out hello)||hello.DeviceId!=receiver.Settings.DeviceId||hello.Fingerprint!=receiver.Security.Fingerprint||SecureIdentity.RemoteFingerprint(stream)!=hello.Fingerprint)throw new IOException("不是本用例的接收节点");
            SecureIdentity.WriteFrame(stream,Protocol.Encode(0,Peer.Login,Peer.Host,Protocol.Entry|Protocol.Utf8,Peer.Nickname,"",Peer.DeviceId,null,Peer.Fingerprint));
            if(SecureIdentity.ReadText(stream,16)!="YES")throw new IOException("配对失败");SecureIdentity.WriteText(stream,"YES");
            if(!SecureIdentity.ReadText(stream,128).StartsWith("F\n",StringComparison.Ordinal)||!Protocol.TryParse(SecureIdentity.ReadFrame(stream,60000),out request)||request.Mode!=Protocol.GetFile||request.Fingerprint!=hello.Fingerprint)throw new IOException("文件请求无效");
            offset=Int64.Parse(request.Body.Split(':')[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture);
            if(offset<0||offset>file.Size)throw new IOException("越界偏移");
            SecureIdentity.WriteText(stream,"OK\n"+file.Size+"\n"+file.Sha256);return stream;
        }
        public void Dispose(){identity.Dispose();}
    }
}
