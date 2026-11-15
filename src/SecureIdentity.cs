using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web.Script.Serialization;

namespace FeiqLight
{
    // 证书私钥只属于本机资料；发现报文中的指纹不是已验证身份，首次信任必须由用户确认。
    public sealed class SecureIdentity : IDisposable
    {
        public sealed class TrustException : IOException { public TrustException(string message):base(message){} }
        private readonly object gate = new object();
        private readonly object pairingGate = new object();
        private int revision;
        private readonly string root;
        private readonly X509Certificate2 certificate;
        private Dictionary<string, string> trusted;
        public readonly string Fingerprint;
        public Func<string, string, bool> ConfirmPeer;
        public event Action<string> TrustRevoked;
        public SecureIdentity(string root)
        {
            this.root = Path.GetFullPath(root); Directory.CreateDirectory(this.root);
            string path = Path.Combine(this.root, "identity.pfx");
            if (File.Exists(path)) certificate = new X509Certificate2(File.ReadAllBytes(path), "", X509KeyStorageFlags.DefaultKeySet);
            else
            {
                using (RSA rsa = RSA.Create())
                {
                    rsa.KeySize = 2048;
                    CertificateRequest request = new CertificateRequest("CN=Feiq", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                    using (X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20)))
                    {
                        byte[] pfx = created.Export(X509ContentType.Pfx, "");
                        AtomicBytes(path, pfx);
                        // .NET Framework 的 Schannel 需要 Windows 密钥句柄；不使用 PersistKeySet，Dispose 时由提供方清理临时密钥。
                        certificate = new X509Certificate2(pfx, "", X509KeyStorageFlags.DefaultKeySet);
                    }
                }
            }
            if (!certificate.HasPrivateKey) throw new IOException("设备证书缺少私钥，请恢复原资料目录。");
            Fingerprint = Hash(certificate.RawData);
            string trustPath = Path.Combine(this.root, "trusted-devices.json");
            trusted = File.Exists(trustPath) ? new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(trustPath, Encoding.UTF8)) : new Dictionary<string, string>();
            if (trusted == null || trusted.Any(p => Protocol.NormalizeDeviceId(p.Key) == null || !ValidFingerprint(p.Value))) throw new IOException("可信设备记录损坏，未自动重置信任。");
        }
        public static bool ValidFingerprint(string value) { return value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')); }
        public static string Hash(byte[] bytes) { using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
        public static string HashFile(string path) { using (FileStream input = File.OpenRead(path)) using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(input)); }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        public static string PairCode(string first, string second)
        {
            string[] values = new[] { first, second }; Array.Sort(values, StringComparer.Ordinal);
            string text = Hash(Encoding.ASCII.GetBytes("Feiq pairing v1\n" + String.Join("\n", values)));
            return String.Join(" ", Enumerable.Range(0, 4).Select(i => text.Substring(i * 4, 4).ToUpperInvariant()));
        }
        public string TrustedFingerprint(string id) { lock (gate) { string value; return id != null && trusted.TryGetValue(id, out value) ? value : null; } }
        public bool IsTrusted(string id, string fingerprint) { return ValidFingerprint(fingerprint) && TrustedFingerprint(id) == fingerprint; }
        public bool IsTrustedFingerprint(string fingerprint) { lock(gate) return ValidFingerprint(fingerprint)&&trusted.ContainsValue(fingerprint); }
        public bool EnsureTrusted(string id, string fingerprint, string name)
        {
            if(IsTrusted(id,fingerprint))return true;
            if(!System.Threading.Monitor.TryEnter(pairingGate))throw new TrustException("已有设备正在核对身份，请完成后重试。");
            try{return ConfirmTrust(id,fingerprint,name);}finally{System.Threading.Monitor.Exit(pairingGate);}
        }
        private bool ConfirmTrust(string id,string fingerprint,string name)
        {
            int version;lock(gate)version=revision;
            if (Protocol.NormalizeDeviceId(id) == null || !ValidFingerprint(fingerprint)) throw new IOException("对方安全身份无效。");
            string previous = TrustedFingerprint(id);
            if (previous != null)
            {
                if (previous != fingerprint) throw new TrustException("对方设备证书已变化，已停止连接。请先核对身份并清除旧信任。");
                return true;
            }
            Func<string, string, bool> confirm = ConfirmPeer;
            if (confirm == null || !confirm(name, PairCode(Fingerprint, fingerprint))) return false;
            lock (gate)
            {
                if(version!=revision)throw new TrustException("配对期间网络或信任已变化，请重新操作。");
                if (trusted.ContainsKey(id) && trusted[id] != fingerprint) throw new IOException("配对期间设备身份发生变化。");
                Dictionary<string, string> next = new Dictionary<string, string>(trusted); next[id] = fingerprint;
                AtomicBytes(Path.Combine(root, "trusted-devices.json"), Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(next)));
                trusted = next;
            }
            return true;
        }
        public void Forget(string id)
        {
            string fingerprint;
            lock (gate)
            {
                revision++;
                trusted.TryGetValue(id,out fingerprint);
                Dictionary<string, string> next = new Dictionary<string, string>(trusted); next.Remove(id);
                AtomicBytes(Path.Combine(root, "trusted-devices.json"), Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(next))); trusted = next;
            }
            Action<string> revoked=TrustRevoked;if(fingerprint!=null&&revoked!=null)revoked(fingerprint);
        }
        public void CancelConfirmations(){lock(gate)revision++;}
        private readonly Dictionary<int, Action<TcpClient>> reverseListeners = new Dictionary<int, Action<TcpClient>>();
        internal void RegisterReverseListener(int port, Action<TcpClient> accept) { lock(gate)reverseListeners.Add(port,accept); }
        internal void UnregisterReverseListener(int port) { lock(gate)reverseListeners.Remove(port); }
        internal bool AcceptReverse(int port, TcpClient client)
        {
            Action<TcpClient> accept;lock(gate)if(!reverseListeners.TryGetValue(port,out accept))return false;
            accept(client);return true;
        }
        public SslStream Client(TcpClient client, string expectedFingerprint)
        {
            if (!ValidFingerprint(expectedFingerprint)) throw new IOException("未取得对方设备证书，不能建立加密连接。");
            SslStream stream = new SslStream(client.GetStream(), false, (sender, remote, chain, errors) => Matches(remote, expectedFingerprint));
            try { using(var timeout=new System.Threading.Timer(_=>client.Close(),null,5000,System.Threading.Timeout.Infinite))stream.AuthenticateAsClient("Feiq", new X509CertificateCollection { certificate }, SslProtocols.Tls12, false); return stream; }
            catch { stream.Dispose(); throw; }
        }
        public SslStream Server(TcpClient client, bool requireClientCertificate)
        {
            SslStream stream = new SslStream(client.GetStream(), false, (sender, remote, chain, errors) => remote==null?!requireClientCertificate:ValidCertificate(remote));
            try { using(var timeout=new System.Threading.Timer(_=>client.Close(),null,5000,System.Threading.Timeout.Infinite))stream.AuthenticateAsServer(certificate, requireClientCertificate, SslProtocols.Tls12, false); return stream; }
            catch { stream.Dispose(); throw; }
        }
        public static string RemoteFingerprint(SslStream stream) { return stream.RemoteCertificate == null ? null : Hash(stream.RemoteCertificate.GetRawCertData()); }
        private static bool Matches(X509Certificate remote, string expected) { return remote != null && ValidCertificate(remote) && Hash(remote.GetRawCertData()) == expected; }
        private static bool ValidCertificate(X509Certificate remote) { using (X509Certificate2 value = new X509Certificate2(remote)) return value.NotBefore.ToUniversalTime() <= DateTime.UtcNow && value.NotAfter.ToUniversalTime() >= DateTime.UtcNow; }
        public static void AtomicBytes(string path, byte[] bytes)
        {
            string temporary = path + ".new";
            using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        public static void WriteFrame(Stream stream, byte[] bytes)
        {
            if (bytes.Length > 4 * 1024 * 1024) throw new IOException("安全报文超过 4 MiB。");
            byte[] size = { (byte)(bytes.Length >> 24), (byte)(bytes.Length >> 16), (byte)(bytes.Length >> 8), (byte)bytes.Length };
            stream.Write(size, 0, size.Length); stream.Write(bytes, 0, bytes.Length); stream.Flush();
        }
        public static byte[] ReadFrame(Stream stream, int maximum)
        {
            byte[] size = ReadBytes(stream, 4); long length = ((long)size[0] << 24) | ((long)size[1] << 16) | ((long)size[2] << 8) | size[3];
            if (length > maximum) throw new IOException("安全报文超过限制。");
            return ReadBytes(stream, (int)length);
        }
        public static byte[] ReadBytes(Stream stream, int length)
        {
            byte[] bytes = new byte[length]; int offset = 0;
            while (offset < length) { int count = stream.Read(bytes, offset, length - offset); if (count == 0) throw new EndOfStreamException("加密连接已断开。"); offset += count; }
            return bytes;
        }
        public static void WriteText(Stream stream, string text) { WriteFrame(stream, Encoding.UTF8.GetBytes(text)); }
        public static string ReadText(Stream stream, int maximum) { return new UTF8Encoding(false, true).GetString(ReadFrame(stream, maximum)); }
        internal sealed class Deadline : Stream
        {
            private readonly Stream source;private readonly System.Diagnostics.Stopwatch clock=System.Diagnostics.Stopwatch.StartNew();private readonly int limit;
            internal Deadline(Stream source,int limit){this.source=source;this.limit=limit;}
            public override int Read(byte[] bytes,int offset,int count){int remaining=limit-(int)clock.ElapsedMilliseconds;if(remaining<=0)throw new IOException("安全握手读取超时。");source.ReadTimeout=remaining;return source.Read(bytes,offset,count);}
            public override bool CanRead{get{return true;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return false;}}public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}public override void Flush(){}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long length){throw new NotSupportedException();}public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
        }
        public void Dispose() { certificate.Dispose(); }
    }
}
