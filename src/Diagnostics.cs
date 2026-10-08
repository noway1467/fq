using System;
using System.IO;
using System.Text;

namespace FeiqLight
{
    // 仅允许固定事件码，不接收异常正文、身份、地址、文件名或聊天内容。
    public enum DiagnosticEvent { Started, NetworkError, FileStatus, DeliveryChanged, Stopped }
    public sealed class Diagnostics
    {
        private static readonly object Gate = new object();
        private readonly string root;
        private readonly Func<bool> enabled;
        public const int Limit = 65536;
        public Diagnostics(string root, Func<bool> enabled) { this.root=Path.Combine(root,"diagnostics");this.enabled=enabled; }
        private string Current { get { return Path.Combine(root,"events.log"); } }
        private string Previous { get { return Path.Combine(root,"previous.log"); } }
        public void Record(DiagnosticEvent code)
        {
            if(!enabled()||!Enum.IsDefined(typeof(DiagnosticEvent),code))return;
            lock(Gate) try {
                Directory.CreateDirectory(root);
                string line=DateTime.UtcNow.ToString("O")+" "+code+Environment.NewLine;
                if(File.Exists(Current)&&new FileInfo(Current).Length+Encoding.UTF8.GetByteCount(line)>Limit) {
                    if(File.Exists(Previous))File.Delete(Previous);File.Move(Current,Previous);
                }
                File.AppendAllText(Current,line,new UTF8Encoding(false));
            } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
        public string Read()
        {
            lock(Gate) {
                StringBuilder text=new StringBuilder("飞Q "+typeof(Diagnostics).Assembly.GetName().Version+" / Windows / UTC\r\n");
                foreach(string file in new[]{Previous,Current}) if(File.Exists(file)) {
                    if(new FileInfo(file).Length>Limit)throw new IOException("诊断文件超过读取限额");
                    text.Append(File.ReadAllText(file,Encoding.UTF8));
                }
                return text.ToString();
            }
        }
        public void Clear() { lock(Gate) {if(File.Exists(Current))File.Delete(Current);if(File.Exists(Previous))File.Delete(Previous);} }
    }
}
