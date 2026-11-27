using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FeiqLight
{
    // 访客模式由用户单独开启，随机能力链接仅限本次选择的文件和接收位置。
    internal sealed class GuestTransfer : IDisposable
    {
        private sealed class Download { internal string Id,Path,Name,Relative; internal long Size,Modified; }
        private sealed class Upload { internal string Id,Name,Partial,Saved,Relative; internal long Size,Offset; internal bool Canceled; }
        private readonly Dictionary<string,Download> downloads=new Dictionary<string,Download>();
        private readonly Dictionary<string,Upload> uploads=new Dictionary<string,Upload>();
        private readonly object gate=new object();
        private readonly ToolsHttp http;
        private readonly Timer expiry;
        private readonly System.Diagnostics.Stopwatch lifetime = System.Diagnostics.Stopwatch.StartNew();
        private readonly long lifetimeMilliseconds;
        private readonly string destination;
        private readonly bool allowUpload;
        private long reserved;
        private int activeUploads;
        private volatile bool closed;
        internal const long UploadQuota=100L*1024*1024*1024;
        internal const int UploadFileLimit=1000, UploadTaskLimit=4096;
        internal readonly string Token;
        internal readonly DateTime Until;
        internal int Port { get { return http.Port; } }
        internal long RemainingMilliseconds { get { return closed ? 0 : Math.Max(0, lifetimeMilliseconds - lifetime.ElapsedMilliseconds); } }
        internal bool Active { get { return RemainingMilliseconds > 0; } }
        internal string[] SavedFiles { get { lock(gate)return uploads.Values.Where(u=>u.Saved!=null).Select(u=>u.Saved).ToArray(); } }
        internal string CleanupError { get; private set; }
        internal Action<string,string> Received;
        internal GuestTransfer(IEnumerable<string> files,string destination,bool allowUpload,bool loopback=false,int lifetimeMinutes=AppSettings.DefaultGuestLifetimeMinutes)
            : this(files.Any()?SendBatch.Expand(files):new List<SendBatch.Item>(),destination,allowUpload,loopback,lifetimeMinutes) { }
        internal GuestTransfer(IEnumerable<SendBatch.Item> files,string destination,bool allowUpload,bool loopback=false,int lifetimeMinutes=AppSettings.DefaultGuestLifetimeMinutes)
        {
            if(lifetimeMinutes<1||lifetimeMinutes>AppSettings.MaxGuestLifetimeMinutes)throw new ArgumentException("请输入 1–1440 的整数分钟数（最多 24 小时）。");
            this.destination=ReceiveStorage.Folder(destination);this.allowUpload=allowUpload;
            if(allowUpload)ReceiveStorage.ValidateWritable(this.destination);
            Token=ToolsService.RandomToken(32);
            foreach(SendBatch.Item source in files) {
                var info=new FileInfo(source.Path);
                if(!info.Exists||!Protocol.SafeFileName(info.Name)||(info.Attributes&FileAttributes.ReparsePoint)!=0||ReceiveStorage.LinkedDirectory(info.DirectoryName)||source.Relative!=null&&(!Protocol.SafeRelativePath(source.Relative)||source.Relative.Split('/').Last()!=info.Name))throw new IOException("请选择可读取且名称安全的普通文件。");
                if(downloads.Count>=1000)throw new IOException("单次最多选择 1000 个文件。");
                string id=Guid.NewGuid().ToString("N");downloads.Add(id,new Download{Id=id,Path=info.FullName,Name=info.Name,Relative=source.Relative,Size=info.Length,Modified=info.LastWriteTimeUtc.Ticks});
            }
            if(downloads.Count==0&&!allowUpload)throw new IOException("请先选择文件或开启接收。");
            Until=DateTime.UtcNow.AddMinutes(lifetimeMinutes);
            lifetimeMilliseconds=lifetimeMinutes*60000L;lifetime.Restart();
            http=new ToolsHttp(0,loopback,Handle,null,1048576);
            expiry=new Timer(_=>Dispose(),null,TimeSpan.FromMinutes(lifetimeMinutes),Timeout.InfiniteTimeSpan);
        }
        internal string Url(string address) { return "http://"+address+":"+Port+"/#"+Token; }
        private void Check() { if(!Active)throw new ToolsHttp.HttpError(410,"临时传输已结束，请让对方重新开启。"); }
        private void Authorize(ToolsHttp.Request request)
        {
            Check();string supplied=request.Header("X-FQ-Guest");
            if(request.Method=="GET"||request.Method=="HEAD")if(supplied.Length==0)supplied=request.Parameter("token");
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
            if(request.Method=="GET"&&request.Path=="/api/guest") {response.Json(new{files=downloads.Values.Select(f=>new{id=f.Id,name=f.Name,relativePath=f.Relative,size=f.Size}),upload=allowUpload,requestIds=true,folders=true,until=Until.ToString("o"),remainingMilliseconds=RemainingMilliseconds,limit=UploadQuota,countLimit=UploadFileLimit});return;}
            if((request.Method=="GET"||request.Method=="HEAD")&&request.Path=="/file"){Send(request,response);return;}
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
                        if(upload.Canceled)throw new ToolsHttp.HttpError(410,"上传已取消。");
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
                Check();if(current.Canceled){if(cancelRequest){response.Json(new{ok=true});return;}throw new ToolsHttp.HttpError(410,"上传已取消。");}
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
        private static bool RequestId(string value) { return value.Length==32&&value.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f'); }
        private void Send(ToolsHttp.Request request,ToolsHttp.Response response)
        {
            Download file;if(!downloads.TryGetValue(request.Parameter("id"),out file))throw new ToolsHttp.HttpError(404,"文件不在本次分享中。");
            var info=new FileInfo(file.Path);if(!info.Exists||info.Length!=file.Size||info.LastWriteTimeUtc.Ticks!=file.Modified||(info.Attributes&FileAttributes.ReparsePoint)!=0||ReceiveStorage.LinkedDirectory(info.DirectoryName))throw new ToolsHttp.HttpError(409,"原文件已变化，请让对方重新选择。");
            using(FileStream input=new FileStream(file.Path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
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
            Upload[] pending;lock(gate){if(closed)return;closed=true;pending=uploads.Values.ToArray();}
            http.Dispose();if(expiry!=null)expiry.Dispose();
            foreach(Upload upload in pending)lock(upload)if(upload.Saved==null&&upload.Partial!=null)try{File.Delete(upload.Partial);}catch(IOException e){CleanupError="未完成文件清理失败："+e.Message;}catch(UnauthorizedAccessException e){CleanupError="未完成文件清理失败："+e.Message;}
        }
    }
}
