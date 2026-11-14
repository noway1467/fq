using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace FeiqLight
{
    // 超时按整条请求计算；滴入字节不能重置期限，缓冲层仍按块读取 socket。
    internal sealed class RequestInput : Stream
    {
        private readonly TcpClient client;
        private readonly NetworkStream source;
        private readonly Stopwatch clock = new Stopwatch();
        private int limit;
        internal RequestInput(TcpClient client) { this.client = client; source = client.GetStream(); }
        internal void Begin(int milliseconds) { limit = milliseconds; clock.Restart(); }
        private void BeforeRead()
        {
            long remaining = limit - clock.ElapsedMilliseconds;
            if (remaining <= 0) throw new IOException("请求读取超时");
            client.ReceiveTimeout = (int)remaining;
        }
        public override int Read(byte[] buffer, int offset, int count) { BeforeRead(); return source.Read(buffer, offset, count); }
        public override int ReadByte() { BeforeRead(); return source.ReadByte(); }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
