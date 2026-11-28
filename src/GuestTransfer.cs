using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace FeiqLight
{
    // 访客模式由用户单独开启，随机能力链接仅限本次选择的文件和接收位置。
    internal sealed class GuestTransfer : IDisposable
    {
        private sealed class Download { internal string Id,Path,Name,Relative; internal long Size,Modified; internal HandleInfo Identity; }
        private sealed class DownloadDirectory
        {
            internal string Id,Path,Name;
            internal readonly List<Download> Files = new List<Download>();
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct HandleInfo
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;
            internal uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder path,uint size,uint flags);
        [DllImport("kernel32.dll", SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file,out HandleInfo info);
        private sealed class Upload { internal string Id,Name,Partial,Saved,Relative; internal long Size,Offset; internal bool Canceled; }
        internal sealed class TextEntry
        {
            internal readonly int Sequence;
            internal readonly string Text;
            internal TextEntry(int sequence,string text) { Sequence=sequence;Text=text; }
        }
        private readonly Dictionary<string,Download> downloads=new Dictionary<string,Download>();
        private readonly Dictionary<string,DownloadDirectory> directories=new Dictionary<string,DownloadDirectory>();
        private readonly Dictionary<string,Upload> uploads=new Dictionary<string,Upload>();
        private readonly object gate=new object();
        private readonly ToolsHttp http;
        private readonly Timer expiry;
        private readonly System.Diagnostics.Stopwatch lifetime = System.Diagnostics.Stopwatch.StartNew();
        private readonly long lifetimeMilliseconds;
        private readonly string destination;
        private readonly bool allowUpload;
        internal readonly bool AllowText;
        private readonly List<TextEntry> publishedTexts=new List<TextEntry>();
        private readonly List<TextEntry> receivedTexts=new List<TextEntry>();
        private int publishedTextBytes,receivedTextBytes;
        private long nextPublishedText,nextReceivedText;
        private bool textsEnded;
        internal event Action TextEnded;
        internal const int TextByteLimit=4096,TextCountLimit=64,TextSessionByteLimit=128*1024;
        internal const int TextIntervalMilliseconds=250;
        private long reserved;
        private int activeUploads;
        private bool archiveBusy;
        private FileStream archiveInput;
        private volatile bool closed;
        internal const long UploadQuota=100L*1024*1024*1024;
        internal const int UploadFileLimit=1000, UploadTaskLimit=4096;
        internal readonly string Token;
        internal readonly DateTime Until;
        internal int Port { get { return http.Port; } }
        internal long RemainingMilliseconds { get { return closed ? 0 : Math.Max(0, lifetimeMilliseconds - lifetime.ElapsedMilliseconds); } }
        internal bool Active { get { bool active=RemainingMilliseconds>0;if(!active)ClearTexts();return active; } }
        internal string[] SavedFiles { get { lock(gate)return uploads.Values.Where(u=>u.Saved!=null).Select(u=>u.Saved).ToArray(); } }
        internal string CleanupError { get; private set; }
        internal Action<string,string> Received;
        internal GuestTransfer(IEnumerable<string> files,string destination,bool allowUpload,bool loopback=false,int lifetimeMinutes=AppSettings.DefaultGuestLifetimeMinutes,bool allowText=false)
            : this(files.Any()?SendBatch.Expand(files):new List<SendBatch.Item>(),destination,allowUpload,loopback,lifetimeMinutes,allowText) { }
        internal GuestTransfer(IEnumerable<SendBatch.Item> files,string destination,bool allowUpload,bool loopback=false,int lifetimeMinutes=AppSettings.DefaultGuestLifetimeMinutes,bool allowText=false)
        {
            if(lifetimeMinutes<1||lifetimeMinutes>AppSettings.MaxGuestLifetimeMinutes)throw new ArgumentException("请输入 1–1440 的整数分钟数（最多 24 小时）。");
            this.destination=ReceiveStorage.Folder(destination);this.allowUpload=allowUpload;AllowText=allowText;
            if(allowUpload)ReceiveStorage.ValidateWritable(this.destination);
            Token=ToolsService.RandomToken(32);
            var directoryPaths=new Dictionary<string,DownloadDirectory>(StringComparer.Ordinal);
            foreach(SendBatch.Item source in files) {
                var info=new FileInfo(source.Path);
                if(!info.Exists||!Protocol.SafeFileName(info.Name)||(info.Attributes&FileAttributes.ReparsePoint)!=0||ReceiveStorage.LinkedDirectory(info.DirectoryName)||source.Relative!=null&&(!Protocol.SafeRelativePath(source.Relative)||source.Relative.Split('/').Last()!=info.Name))throw new IOException("请选择可读取且名称安全的普通文件。");
                if(downloads.Count>=1000)throw new IOException("单次最多选择 1000 个文件。");
                string id=Guid.NewGuid().ToString("N");
                var file=new Download{Id=id,Path=info.FullName,Name=info.Name,Relative=source.Relative,Size=info.Length,Modified=info.LastWriteTimeUtc.Ticks};
                using(var input=new FileStream(file.Path,FileMode.Open,FileAccess.Read,FileShare.Read))file.Identity=ReadHandle(file,input);
                downloads.Add(id,file);
                if(file.Relative!=null) {
                    string[] parts=file.Relative.Split('/');string path="";
                    for(int index=0;index<parts.Length-1;index++) {
                        path=path.Length==0?parts[index]:path+"/"+parts[index];
                        if(index!=0&&index!=parts.Length-2)continue;
                        DownloadDirectory directory;
                        if(!directoryPaths.TryGetValue(path,out directory)) {
                            directory=new DownloadDirectory{Id=Guid.NewGuid().ToString("N"),Path=path,Name=parts[index]};
                            directoryPaths.Add(path,directory);directories.Add(directory.Id,directory);
                        }
                    }
                }
            }
            // 入口只登记分享根和有直接文件的目录，再用冻结清单填满各入口的授权子树。
            foreach(Download file in downloads.Values)if(file.Relative!=null) {
                string[] parts=file.Relative.Split('/');string path="";
                for(int index=0;index<parts.Length-1;index++) {
                    path=path.Length==0?parts[index]:path+"/"+parts[index];
                    DownloadDirectory directory;if(directoryPaths.TryGetValue(path,out directory))directory.Files.Add(file);
                }
            }
            if(downloads.Count==0&&!allowUpload&&!allowText)throw new IOException("请先选择文件、开启接收或允许文本互传。");
            Until=DateTime.UtcNow.AddMinutes(lifetimeMinutes);
            lifetimeMilliseconds=lifetimeMinutes*60000L;lifetime.Restart();
            http=new ToolsHttp(0,loopback,Handle,null,1048576);
            expiry=new Timer(_=>Dispose(),null,TimeSpan.FromMinutes(lifetimeMinutes),Timeout.InfiniteTimeSpan);
        }
        internal string Url(string address) { return "http://"+address+":"+Port+"/#"+Token; }
        private void Check() { if(!Active)throw new ToolsHttp.HttpError(410,"临时传输已结束，请让对方重新开启。","sessionEnded"); }
        private void Authorize(ToolsHttp.Request request)
        {
            Check();string supplied=request.Header("X-FQ-Guest");
            if(request.Path!="/api/text"&&(request.Method=="GET"||request.Method=="HEAD"))if(supplied.Length==0)supplied=request.Parameter("token");
            if(supplied!=Token)throw new ToolsHttp.HttpError(403,"链接无效，请使用对方分享的完整链接。");
        }
        private void Handle(ToolsHttp.Request request,ToolsHttp.Response response)
        {
            Check();
            if(request.Method=="GET"&&(request.Path=="/"||request.Path=="/guest.css"||request.Path=="/guest.js"||request.Path=="/qrcodegen.js")) {
                string name=request.Path=="/"?"guest.html":request.Path.Substring(1);
                using(Stream input=typeof(GuestTransfer).Assembly.GetManifestResourceStream("FeiqLight.Tools."+name)) {
                    if(input==null)throw new IOException("网页资源缺失。");using(var buffer=new MemoryStream()){input.CopyTo(buffer);response.Bytes(buffer.ToArray(),name.EndsWith(".js")?"application/javascript; charset=utf-8":name.EndsWith(".css")?"text/css; charset=utf-8":"text/html; charset=utf-8");}
                }return;
            }
            if(request.Path=="/favicon.ico"){response.Bytes(new byte[0],"image/x-icon",204);return;}
            Authorize(request);
            if(request.Method=="GET"&&request.Path=="/api/guest") {
                var status=new {
                    files=downloads.Values.Select(f=>new{id=f.Id,name=f.Name,relativePath=f.Relative,size=f.Size}),
                    directories=directories.Values.Select(d=>new{id=d.Id,name=d.Name,relativePath=d.Path,count=d.Files.Count,size=d.Files.Sum(f=>f.Size)}),
                    archive=new{enabled=true,maxEntries=StoreZipStream.MaxEntries,maxBytes=StoreZipStream.MaxBytes},upload=allowUpload,text=AllowText,
                    textLimits=new{maxBytes=TextByteLimit,maxEntries=TextCountLimit,maxTotalBytes=TextSessionByteLimit,intervalMilliseconds=TextIntervalMilliseconds},requestIds=true,folders=true,
                    until=Until.ToString("o"),remainingMilliseconds=RemainingMilliseconds,limit=UploadQuota,countLimit=UploadFileLimit
                };
                // 1000 个长路径及最多 2000 个入口允许超过默认约 200 万字符；JSON 转义也计入预算。
                response.Bytes(Encoding.UTF8.GetBytes(new JavaScriptSerializer{MaxJsonLength=32*1024*1024}.Serialize(status)),"application/json; charset=utf-8");return;
            }
            if((request.Method=="GET"||request.Method=="HEAD")&&request.Path=="/file"){Send(request,response);return;}
            if(request.Method=="GET"&&request.Path=="/archive"){Archive(request,response);return;}
            if(request.Path=="/api/text"){HandleText(request,response);return;}
            if(!allowUpload)throw new ToolsHttp.HttpError(403,"对方未开启浏览器接收。");
            if(request.Method=="POST"&&request.Path=="/api/prepare") {
                var data=request.Json();string name=Convert.ToString(data.ContainsKey("name")?data["name"]:null);long size;
                if(!Protocol.SafeFileName(name)||!Int64.TryParse(Convert.ToString(data.ContainsKey("size")?data["size"]:null),out size)||size<0||size>UploadQuota)throw new ToolsHttp.HttpError(400,"文件名或大小无效。");
                string key=data.ContainsKey("requestId")?Convert.ToString(data["requestId"]):null;
                string relative=data.ContainsKey("relativePath")?Convert.ToString(data["relativePath"]):null;
                if(key!=null&&!RequestId(key)||relative!=null&&(!Protocol.SafeRelativePath(relative)||relative.Split('/').Last()!=name))throw new ToolsHttp.HttpError(400,"上传标识或目录结构无效。");
                Upload upload;
                lock(gate) {
                    if(key!=null&&uploads.TryGetValue(key,out upload)) {
                        if(upload.Canceled)throw new ToolsHttp.HttpError(410,"上传已取消。","taskCanceled");
                        if(upload.Name!=name||upload.Size!=size||upload.Relative!=relative)throw new ToolsHttp.HttpError(409,"上传标识已用于其他文件。");
                        response.Json(new{id=upload.Id,offset=0});return;
                    }
                    Check();if(uploads.Count>=UploadTaskLimit)throw new ToolsHttp.HttpError(409,"本次上传任务过多，请重新开启临时传输。");
                    if(activeUploads>=UploadFileLimit||size>UploadQuota-reserved)throw new ToolsHttp.HttpError(409,"本次接收已达 1000 个文件或 100 GiB 上限，请重新开启。");
                    if(new DriveInfo(Path.GetPathRoot(destination)).AvailableFreeSpace<size+16L*1024*1024)throw new ToolsHttp.HttpError(409,"接收设备剩余空间不足。");
                    string id=key??Guid.NewGuid().ToString("N");upload=new Upload{Id=id,Name=name,Relative=relative,Size=size,Partial=Path.Combine(destination,".fq-guest-"+id+".part")};
                    using(FileStream create=new FileStream(upload.Partial,FileMode.CreateNew,FileAccess.Write)){}uploads.Add(id,upload);reserved+=size;activeUploads++;
                }
                response.Json(new{id=upload.Id,offset=0});return;
            }
            string cancelKey=request.Parameter("requestId");bool cancelRequest=request.Method=="POST"&&request.Path=="/api/cancel"&&cancelKey.Length>0;
            if(cancelRequest&&!RequestId(cancelKey))throw new ToolsHttp.HttpError(400,"上传标识无效。");
            Upload current;
            lock(gate) {
                if(!uploads.TryGetValue(cancelRequest?cancelKey:request.Parameter("id"),out current)) {
                    if(!cancelRequest)throw new ToolsHttp.HttpError(404,"上传任务不存在。");
                    if(uploads.Count>=UploadTaskLimit)throw new ToolsHttp.HttpError(409,"本次上传任务过多，请重新开启临时传输。");
                    // 取消可先于准备到达；保留有界记录，阻止晚到请求重新预留配额。
                    current=new Upload{Id=cancelKey,Canceled=true};uploads.Add(cancelKey,current);
                }
            }
            lock(current) {
                Check();if(current.Canceled){if(cancelRequest){response.Json(new{ok=true});return;}throw new ToolsHttp.HttpError(410,"上传已取消。","taskCanceled");}
                if(request.Method=="GET"&&request.Path=="/api/upload-status"){response.Json(new{offset=current.Offset,complete=current.Saved!=null});return;}
                if(request.Method=="POST"&&request.Path=="/api/cancel") {
                    if(current.Saved!=null)throw new ToolsHttp.HttpError(409,"文件已保存。");
                    File.Delete(current.Partial);
                    lock(gate){current.Canceled=true;reserved=Math.Max(0,reserved-current.Size);activeUploads--;}
                    response.Json(new{ok=true});return;
                }
                if(current.Saved!=null){response.Json(new{offset=current.Offset,complete=true});return;}
                if(request.Method=="POST"&&request.Path=="/api/chunk") {
                    long offset;if(!Int64.TryParse(request.Parameter("offset"),out offset)||offset!=current.Offset||request.Body.Length==0||request.Body.LongLength>current.Size-current.Offset)throw new ToolsHttp.HttpError(409,"上传位置已变化，请重试。");
                    if(new DriveInfo(Path.GetPathRoot(destination)).AvailableFreeSpace<request.Body.LongLength+16L*1024*1024)throw new ToolsHttp.HttpError(409,"接收设备空间不足。");
                    using(FileStream output=new FileStream(current.Partial,FileMode.Open,FileAccess.Write,FileShare.None)){output.Position=current.Offset;output.Write(request.Body,0,request.Body.Length);output.Flush(true);current.Offset=output.Length;}
                    response.Json(new{offset=current.Offset});return;
                }
                if(request.Method=="POST"&&request.Path=="/api/finish") {
                    if(current.Offset!=current.Size)throw new ToolsHttp.HttpError(409,"文件尚未完整上传。");
                    string target=SendBatch.Destination(destination,new Attachment{Name=current.Name,RelativePath=current.Relative}),original=target;int suffix=0;
                    if(ReceiveStorage.LinkedDirectory(Path.GetDirectoryName(target)))throw new IOException("接收目录不能是链接。");
                    lock(gate){while(true){Check();try{File.Move(current.Partial,target);break;}catch(IOException){if((!File.Exists(target)&&!Directory.Exists(target))||++suffix>10000)throw;target=Path.Combine(Path.GetDirectoryName(original),Path.GetFileNameWithoutExtension(original)+" ("+suffix+")"+Path.GetExtension(original));}}current.Saved=target;}
                    if(Received!=null)Received(target,Path.GetFileName(target));response.Json(new{complete=true,name=Path.GetFileName(target)});return;
                }
                throw new ToolsHttp.HttpError(404,"没有此操作。");
            }
        }
        internal TextEntry[] Texts(bool published)
        {
            if(!Active)return new TextEntry[0];
            lock(gate)return textsEnded?new TextEntry[0]:(published?publishedTexts:receivedTexts).ToArray();
        }
        internal void PublishText(string text)
        {
            lock(gate){CheckText();AddText(publishedTexts,text,ref publishedTextBytes,ref nextPublishedText);}
        }
        private void CheckText()
        {
            Check();
            if(textsEnded)throw new ToolsHttp.HttpError(410,"临时传输已结束，请让对方重新开启。","sessionEnded");
            if(!AllowText)throw new ToolsHttp.HttpError(403,"对方未开启文本互传。");
        }
        private void AddText(List<TextEntry> entries,string text,ref int total,ref long next)
        {
            if(string.IsNullOrEmpty(text)||text.All(c=>c=='\0'))throw new ToolsHttp.HttpError(400,"请输入非空的纯文本。");
            int bytes;
            try{bytes=new UTF8Encoding(false,true).GetByteCount(text);}
            catch(EncoderFallbackException){throw new ToolsHttp.HttpError(400,"文本包含无效的 Unicode 字符。");}
            if(bytes>TextByteLimit)throw new ToolsHttp.HttpError(413,"单条文本最多 4096 UTF-8 字节。");
            if(entries.Count>=TextCountLimit||bytes>TextSessionByteLimit-total)throw new ToolsHttp.HttpError(409,"本方向已达 64 条或 128 KiB 文本上限，请重新开启会话。");
            long now=lifetime.ElapsedMilliseconds;
            if(now<next)throw new ToolsHttp.HttpError(429,"发送过于频繁，请稍等片刻再发送（每 250 毫秒最多一条）。");
            entries.Add(new TextEntry(entries.Count+1,text));total+=bytes;next=now+TextIntervalMilliseconds;
        }
        private void HandleText(ToolsHttp.Request request,ToolsHttp.Response response)
        {
            CheckText();
            if(request.Method=="GET") {
                    string value=request.Parameter("after");int after=0;
                    if(value.Length>0&&(!value.All(c=>c>='0'&&c<='9')||!Int32.TryParse(value,out after)||after>TextCountLimit))throw new ToolsHttp.HttpError(400,"文本读取位置无效。");
                    // 收件箱只供本机界面读取，访客永远只能读取主机主动发布的队列。
                    TextEntry[] entries;int last;
                    lock(gate){CheckText();entries=publishedTexts.Where(t=>t.Sequence>after).ToArray();last=publishedTexts.Count;}
                    CheckText();response.Json(new{items=entries.Select(t=>new{seq=t.Sequence,text=t.Text}),last=last});return;
            }
            if(request.Method=="POST") {
                    if(!request.Header("Content-Type").StartsWith("application/json",StringComparison.OrdinalIgnoreCase))throw new ToolsHttp.HttpError(400,"请使用 JSON 请求体发送纯文本。");
                    if(request.Body.Length>32768)throw new ToolsHttp.HttpError(413,"单条文本最多 4096 UTF-8 字节。");
                    Dictionary<string,object> data;
                    try{data=new JavaScriptSerializer{MaxJsonLength=32768,RecursionLimit=8}.Deserialize<Dictionary<string,object>>(new UTF8Encoding(false,true).GetString(request.Body));}
                    catch(ArgumentException){throw new ToolsHttp.HttpError(400,"文本请求内容无效。");}
                    catch(InvalidOperationException){throw new ToolsHttp.HttpError(400,"文本请求内容无效。");}
                    object value;
                    if(data==null||!data.TryGetValue("text",out value)||!(value is string))throw new ToolsHttp.HttpError(400,"请输入非空的纯文本。");
                    lock(gate){CheckText();AddText(receivedTexts,(string)value,ref receivedTextBytes,ref nextReceivedText);}
                    response.Json(new{ok=true});return;
            }
            throw new ToolsHttp.HttpError(405,"文本互传仅支持读取或发送。");
        }
        private void ClearTexts()
        {
            Action ended;
            lock(gate) {
                if(textsEnded)return;
                textsEnded=true;publishedTexts.Clear();receivedTexts.Clear();publishedTextBytes=receivedTextBytes=0;nextPublishedText=nextReceivedText=0;
                ended=TextEnded;TextEnded=null;
            }
            if(ended!=null)ended();
        }
        private static bool RequestId(string value) { return value.Length==32&&value.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f'); }
        private static void CheckSource(Download file)
        {
            var info=new FileInfo(file.Path);
            if(!info.Exists||info.Length!=file.Size||info.LastWriteTimeUtc.Ticks!=file.Modified||(info.Attributes&FileAttributes.ReparsePoint)!=0||ReceiveStorage.LinkedDirectory(info.DirectoryName))
                throw new ToolsHttp.HttpError(409,"原文件已变化，请让对方重新选择。");
        }
        private static HandleInfo ReadHandle(Download file,FileStream input)
        {
            CheckSource(file);
            var actual=new StringBuilder(32768);
            uint count=GetFinalPathNameByHandle(input.SafeFileHandle,actual,(uint)actual.Capacity,0);
            string resolved=actual.ToString();
            if(resolved.StartsWith("\\\\?\\UNC\\"))resolved="\\\\"+resolved.Substring(8);
            else if(resolved.StartsWith("\\\\?\\"))resolved=resolved.Substring(4);
            HandleInfo info;
            if(count==0||count>=actual.Capacity||!String.Equals(resolved,file.Path,StringComparison.OrdinalIgnoreCase)||!GetFileInformationByHandle(input.SafeFileHandle,out info))
                throw new ToolsHttp.HttpError(409,"文件来源已离开本次授权位置，请重新选择。");
            long size=((long)info.SizeHigh<<32)|info.SizeLow;
            long written=((long)(uint)info.Written.dwHighDateTime<<32)|(uint)info.Written.dwLowDateTime;
            if((info.Attributes&(uint)(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0||size!=file.Size||written!=new DateTime(file.Modified,DateTimeKind.Utc).ToFileTimeUtc())
                throw new ToolsHttp.HttpError(409,"原文件已变化，请让对方重新选择。");
            return info;
        }
        private static void ValidateSource(Download file,Stream input)
        {
            if(input==null) {
                CheckSource(file);
                using(var probe=new FileStream(file.Path,FileMode.Open,FileAccess.Read,FileShare.Read))ValidateSource(file,probe);
                return;
            }
            HandleInfo info=ReadHandle(file,(FileStream)input);
            if(info.Volume!=file.Identity.Volume||info.IndexHigh!=file.Identity.IndexHigh||info.IndexLow!=file.Identity.IndexLow)
                throw new ToolsHttp.HttpError(409,"原文件已被替换，请让对方重新选择。");
        }
        private Stream OpenArchiveSource(Download file)
        {
            Check();CheckSource(file);
            var input=new FileStream(file.Path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan);
            try {
                ValidateSource(file,input);
                lock(gate){Check();archiveInput=input;}
                return input;
            } catch { input.Dispose();throw; }
        }
        private void Archive(ToolsHttp.Request request,ToolsHttp.Response response)
        {
            string scope=request.Parameter("scope"),id=request.Parameter("dir");
            DownloadDirectory directory=null;
            if(scope=="all"&&id.Length==0) { }
            else if(scope.Length==0&&id.Length>0) {
                if(!directories.TryGetValue(id,out directory))throw new ToolsHttp.HttpError(404,"目录不在本次分享中。");
            } else throw new ToolsHttp.HttpError(400,"请选择下载全部或本次分享的目录。");
            if(request.Header("Range").Length>0)throw new ToolsHttp.HttpError(416,"打包下载暂不支持续传，请重新下载。");
            Download[] files=directory==null?downloads.Values.ToArray():directory.Files.ToArray();
            int prefix=directory==null?0:directory.Path.LastIndexOf('/')+1;
            lock(gate) {
                Check();
                if(archiveBusy)throw new ToolsHttp.HttpError(409,"已有一个打包下载正在进行，请等它完成后重试。");
                archiveBusy=true;
            }
            try {
                Action check=()=>{Check();response.CheckConnection();};
                StoreZipStream archive;
                try {
                    archive=new StoreZipStream(files.Select(file=>new StoreZipStream.Entry(
                        directory==null?(file.Relative??file.Name):file.Relative.Substring(prefix),file.Size,new DateTime(file.Modified,DateTimeKind.Utc),
                        ()=>OpenArchiveSource(file),input=>ValidateSource(file,input))));
                } catch(InvalidDataException error) { throw new ToolsHttp.HttpError(409,error.Message); }
                try { archive.Prepare(check); }
                catch(IOException) { throw new ToolsHttp.HttpError(409,"原文件已变化或不可用，请让对方重新选择。"); }
                catch(UnauthorizedAccessException) { throw new ToolsHttp.HttpError(409,"原文件不可读取，请让对方重新选择。"); }
                check();
                string name=directory==null?"飞Q-全部文件.zip":directory.Name+".zip";
                string encoded=Uri.EscapeDataString(name).Replace("'","%27").Replace("(","%28").Replace(")","%29");
                response.Begin(200,"application/zip",archive.ContentLength,"Content-Disposition: attachment; filename*=UTF-8''"+encoded+"\r\n");
                archive.Write((bytes,count)=>{check();response.Write(bytes,count);},check);
            } finally { lock(gate){archiveInput=null;archiveBusy=false;} }
        }
        private void Send(ToolsHttp.Request request,ToolsHttp.Response response)
        {
            Download file;if(!downloads.TryGetValue(request.Parameter("id"),out file))throw new ToolsHttp.HttpError(404,"文件不在本次分享中。");
            var info=new FileInfo(file.Path);if(!info.Exists||info.Length!=file.Size||info.LastWriteTimeUtc.Ticks!=file.Modified||(info.Attributes&FileAttributes.ReparsePoint)!=0||ReceiveStorage.LinkedDirectory(info.DirectoryName))throw new ToolsHttp.HttpError(409,"原文件已变化，请让对方重新选择。");
            using(FileStream input=new FileStream(file.Path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                ValidateSource(file,input);
                long[] range=ToolsHttp.Range(request.Header("Range"),file.Size);if(range==null){response.Bytes(new byte[0],"application/octet-stream",416,"Content-Range: bytes */"+file.Size+"\r\n");return;}
                input.Position=range[0];long left=Math.Max(0,range[1]-range[0]+1);
                string headers="Content-Disposition: attachment; filename*=UTF-8''"+Uri.EscapeDataString(file.Name)+"\r\nAccept-Ranges: bytes\r\n";
                if(request.Header("Range").Length>0)headers+="Content-Range: bytes "+range[0]+"-"+range[1]+"/"+file.Size+"\r\n";
                response.Begin(request.Header("Range").Length==0?200:206,"application/octet-stream",left,headers);if(request.Method=="HEAD")return;
                byte[] buffer=new byte[262144];while(left>0){Check();int count=input.Read(buffer,0,(int)Math.Min(left,buffer.Length));if(count==0)throw new EndOfStreamException();response.Write(buffer,count);left-=count;}
            }
        }
        public void Dispose()
        {
            Upload[] pending;FileStream input;lock(gate){if(closed)return;closed=true;pending=uploads.Values.ToArray();input=archiveInput;archiveInput=null;}
            ClearTexts();
            http.Dispose();if(expiry!=null)expiry.Dispose();
            if(input!=null)input.Dispose();
            foreach(Upload upload in pending)lock(upload)if(upload.Saved==null&&upload.Partial!=null)try{File.Delete(upload.Partial);}catch(IOException e){CleanupError="未完成文件清理失败："+e.Message;}catch(UnauthorizedAccessException e){CleanupError="未完成文件清理失败："+e.Message;}
        }
    }
}
