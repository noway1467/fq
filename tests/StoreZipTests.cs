using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace FeiqLight.Tests
{
    internal static class StoreZipTests
    {
        private static readonly DateTime Modified = new DateTime(2026, 11, 28, 8, 9, 10, DateTimeKind.Utc);
        private static readonly string[] Names = {
            "资料 中文/子目录/特殊 +&%#😀.txt", "资料 中文/空文件.txt", "README.txt", "readme.TXT",
            "同名", "同名/子目录/文件.txt", "目录/文.txt", "目录/文.txt", "Case/文件.txt", "case/文件.txt"
        };
        private static readonly byte[][] Contents = {
            Encoding.UTF8.GetBytes("多级中文与特殊符号 😀 +&%#"), new byte[0], Encoding.ASCII.GetBytes("123456789"),
            Encoding.UTF8.GetBytes("大小写同名"), Encoding.UTF8.GetBytes("同名文件"), Encoding.UTF8.GetBytes("同名目录内文件"),
            new byte[]{0,1,2,3,255}, new byte[]{4,5,6}, Encoding.UTF8.GetBytes("Case"), Encoding.UTF8.GetBytes("case")
        };
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Reject<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("未拒绝无效或中断的归档：" + typeof(T).Name);
        }
        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return String.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2")));
        }
        private static uint Crc(byte[] bytes)
        {
            uint crc = UInt32.MaxValue;
            foreach (byte value in bytes) {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320U);
            }
            return crc ^ UInt32.MaxValue;
        }
        private static StoreZipStream.Entry Entry(string path, byte[] data, Action<Stream> validate = null)
        {
            return new StoreZipStream.Entry(path, data.LongLength, Modified, () => new MemoryStream(data, false), validate ?? (input => { }));
        }
        private static StoreZipStream Fixture()
        {
            return new StoreZipStream(Names.Select((name, index) => Entry(name, Contents[index])));
        }
        private static byte[] Encode(StoreZipStream zip)
        {
            zip.Prepare(() => { });
            using (var output = new MemoryStream()) {
                zip.Write((bytes, length) => output.Write(bytes, 0, length), () => { });
                Check(output.Length == zip.ContentLength, "预计算 Content-Length 与实际字节不符");
                return output.ToArray();
            }
        }
        private static bool Eocd(byte[] bytes)
        {
            return bytes.Length >= 22 && BitConverter.ToUInt32(bytes, bytes.Length - 22) == 0x06054b50;
        }
        private static void Verify(byte[] bytes, string[] paths, byte[][] contents)
        {
            Check(Eocd(bytes), "缺少完整 EOCD");
            int local = 0, central = (int)BitConverter.ToUInt32(bytes, bytes.Length - 6);
            Check(BitConverter.ToUInt16(bytes, bytes.Length - 14) == paths.Length, "EOCD 条目数不符");
            using (var input = new MemoryStream(bytes))
            using (var archive = new ZipArchive(input, ZipArchiveMode.Read, false, Encoding.UTF8)) {
                Check(archive.Entries.Count == paths.Length, ".NET ZipArchive 丢失条目");
                for (int index = 0; index < paths.Length; index++) {
                    ZipArchiveEntry entry = archive.Entries[index];
                    Check(entry.FullName == paths[index], "UTF-8 路径或同名避让不符：" + entry.FullName);
                    byte[] decoded;
                    using (Stream source = entry.Open()) using (var data = new MemoryStream()) { source.CopyTo(data); decoded = data.ToArray(); }
                    Check(Hash(decoded) == Hash(contents[index]), "解压 SHA-256 不符：" + entry.FullName);
                    uint crc = Crc(decoded);
                    Check(BitConverter.ToUInt32(bytes, local) == 0x04034b50 && BitConverter.ToUInt16(bytes, local + 8) == 0, "不是 Store 本地头");
                    Check(BitConverter.ToUInt16(bytes, local + 6) == 0x0800, "本地头必须 UTF-8 且没有 Data Descriptor");
                    Check(BitConverter.ToUInt32(bytes, local + 14) == crc && BitConverter.ToUInt32(bytes, local + 18) == decoded.Length && BitConverter.ToUInt32(bytes, local + 22) == decoded.Length, "本地头 CRC/长度不符");
                    int nameLength = BitConverter.ToUInt16(bytes, local + 26);
                    Check(Encoding.UTF8.GetString(bytes, local + 30, nameLength) == paths[index], "本地头名称不符");
                    Check(BitConverter.ToUInt32(bytes, central) == 0x02014b50 && BitConverter.ToUInt16(bytes, central + 10) == 0 && BitConverter.ToUInt16(bytes, central + 8) == 0x0800, "中央目录方法或标志位不符");
                    Check(BitConverter.ToUInt32(bytes, central + 16) == crc && BitConverter.ToUInt32(bytes, central + 42) == local, "中央目录 CRC/偏移不符");
                    local += 30 + nameLength + decoded.Length;
                    central += 46 + BitConverter.ToUInt16(bytes, central + 28);
                }
            }
            Check(local == BitConverter.ToUInt32(bytes, bytes.Length - 6) && central == bytes.Length - 22, "中央目录或 EOCD 边界不符");
        }
        internal static void GenerateFixture(string directory)
        {
            Directory.CreateDirectory(directory);
            StoreZipStream zip = Fixture();
            byte[] bytes = Encode(zip);
            Verify(bytes, zip.Paths, Contents);
            File.WriteAllBytes(Path.Combine(directory, "windows-store.zip"), bytes);
            string[] manifest = zip.Paths.Select((path, index) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path)) + "\t" + Hash(Contents[index])).ToArray();
            File.WriteAllLines(Path.Combine(directory, "windows-store.tsv"), manifest, new UTF8Encoding(false));
        }
        internal static void VerifyArtifact(string zipPath, string manifestPath)
        {
            string[][] manifest = File.ReadAllLines(manifestPath, Encoding.UTF8).Select(line => line.Split('\t')).ToArray();
            byte[] bytes = File.ReadAllBytes(zipPath);
            var decoded = new List<byte[]>();
            using (var input = new MemoryStream(bytes)) using (var archive = new ZipArchive(input, ZipArchiveMode.Read, false, Encoding.UTF8)) {
                Check(archive.Entries.Count == manifest.Length, "外部归档条目数不符");
                for (int index = 0; index < manifest.Length; index++) {
                    ZipArchiveEntry entry = archive.Entries[index];
                    Check(entry.FullName == Encoding.UTF8.GetString(Convert.FromBase64String(manifest[index][0])), "外部归档名称不符");
                    using (Stream source = entry.Open()) using (var data = new MemoryStream()) {
                        source.CopyTo(data);byte[] content = data.ToArray();
                        Check(Hash(content) == manifest[index][1], "外部归档 SHA-256 不符");decoded.Add(content);
                    }
                }
            }
            Verify(bytes, manifest.Select(row => Encoding.UTF8.GetString(Convert.FromBase64String(row[0]))).ToArray(), decoded.ToArray());
        }
        internal static void RunAll(Action<string, Action> run, string root)
        {
            Directory.CreateDirectory(root);
            run("Store ZIP：.NET ZipArchive 解压、CRC/SHA-256、UTF-8、大小写和文件目录同名避让", () => {
                StoreZipStream zip = Fixture();byte[] bytes = Encode(zip);Verify(bytes, zip.Paths, Contents);
                Check(Crc(Encoding.ASCII.GetBytes("123456789")) == 0xcbf43926, "CRC-32 标准向量不符");
                Check(zip.Paths.Contains("readme (1).TXT") && zip.Paths.Contains("同名 (1)") && zip.Paths.Contains("case (1)/文件.txt"), "同名避让不稳定");
                Check(zip.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == zip.Paths.Length, "存在大小写重复路径");
                foreach (string path in zip.Paths) Check(Protocol.SafeRelativePath(path) && !zip.Paths.Any(other => other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)), "文件目录冲突或路径不安全");
                string artifact = Environment.GetEnvironmentVariable("FQ_ZIP_ARTIFACT_DIR");if (!String.IsNullOrEmpty(artifact)) GenerateFixture(artifact);
                string longName = new string('a', 236) + ".txt";
                var longZip = new StoreZipStream(new[]{Entry(longName,new byte[0]),Entry(longName,new byte[0])});
                Verify(Encode(longZip),longZip.Paths,new[]{new byte[0],new byte[0]});Check(longZip.Paths.All(Protocol.SafeRelativePath),"长文件名避让破坏路径约束");
            });
            run("Store ZIP：空清单、1000 项、ZIP32 单文件/中央目录/总长哨兵在读取前校验", () => {
                int opens = 0;
                Func<long,StoreZipStream.Entry> sized = value => new StoreZipStream.Entry("a",value,Modified,()=>{opens++;throw new Exception("不得打开越界来源");},input=>{});
                Reject<InvalidDataException>(()=>new StoreZipStream(new StoreZipStream.Entry[0]));
                Reject<InvalidDataException>(()=>new StoreZipStream(new[]{sized(-1)}));
                Reject<InvalidDataException>(()=>new StoreZipStream(new[]{sized(StoreZipStream.Zip32Sentinel)}));
                Reject<InvalidDataException>(()=>new StoreZipStream(new[]{sized(Int64.MaxValue)}));
                Reject<InvalidDataException>(()=>new StoreZipStream(new[]{sized(StoreZipStream.Zip32Sentinel-30)}));
                Reject<InvalidDataException>(()=>new StoreZipStream(new[]{sized(StoreZipStream.Zip32Sentinel-100)}));
                var boundary=new StoreZipStream(new[]{sized(StoreZipStream.Zip32Sentinel-101)});Check(boundary.ContentLength==StoreZipStream.MaxBytes,"最大合法总长被误拒绝");
                new StoreZipStream(Enumerable.Range(0,1000).Select(index=>Entry(index+".txt",new byte[0])));
                string nested=String.Join("/",Enumerable.Repeat(new string('a',58),30));
                var deep=new StoreZipStream(Enumerable.Range(0,1000).Select(index=>Entry("r"+index+"/"+nested+"/a.txt",new byte[0])));
                Check(deep.Paths.Length==1000&&deep.Paths.All(Protocol.SafeRelativePath),"深层名称树规划不完整");
                Reject<InvalidDataException>(()=>new StoreZipStream(Enumerable.Range(0,1001).Select(index=>Entry(index+".txt",new byte[0]))));
                Check(opens==0,"越界预检读取了来源");
                foreach(string path in new[]{"../a","/a","a//b","a\\b","C:/a","CON.txt",String.Join("/",Enumerable.Repeat("a",33)),new string('a',241),"bad\ud800"})
                    Reject<InvalidDataException>(()=>new StoreZipStream(new[]{Entry(path,new byte[0])}));
            });
            run("Store ZIP：两遍读取同长变更 CRC 不符时断流无 EOCD", () => {
                byte[] data=Encoding.ASCII.GetBytes("123456789");int opens=0;
                var entry=new StoreZipStream.Entry("a.txt",data.Length,Modified,()=>{opens++;return new MemoryStream(data,false);},input=>{});
                var zip=new StoreZipStream(new[]{entry});zip.Prepare(()=>{});data=(byte[])data.Clone();data[0]^=1;
                using(var output=new MemoryStream()){Reject<IOException>(()=>zip.Write((bytes,count)=>output.Write(bytes,0,count),()=>{}));Check(!Eocd(output.ToArray()),"CRC 变化仍写出 EOCD");}
                Check(opens==2,"编码器必须恰好读取内容两遍");
            });
            run("Store ZIP：预检和发送时长度不符均拒绝，发送中不补写 EOCD", () => {
                foreach(int delta in new[]{-1,1}) {
                    byte[] data=new byte[8];var entry=new StoreZipStream.Entry("a",8,Modified,()=>new MemoryStream(data,false),input=>{});
                    var zip=new StoreZipStream(new[]{entry});zip.Prepare(()=>{});data=new byte[8+delta];
                    using(var output=new MemoryStream()){Reject<IOException>(()=>zip.Write((bytes,count)=>output.Write(bytes,0,count),()=>{}));Check(!Eocd(output.ToArray()),"长度变化仍完成 ZIP");}
                    Reject<IOException>(()=>new StoreZipStream(new[]{entry}).Prepare(()=>{}));
                }
            });
            run("Store ZIP：每块与中央目录后复核来源，过期、停止和写入失败均无 EOCD", () => {
                foreach(string trigger in new[]{"block","central","expiry","disconnect"}) {
                    bool invalid=false;byte[] content=new byte[524305];
                    var zip=new StoreZipStream(new[]{Entry("a.bin",content,input=>{if(invalid&&trigger!="expiry")throw new IOException("来源已变化");})});
                    Action check=()=>{if(invalid&&trigger=="expiry")throw new ToolsHttp.HttpError(410,"临时传输已结束");};zip.Prepare(check);
                    using(var output=new MemoryStream()) {
                        Action action=()=>zip.Write((bytes,count)=>{
                            if(trigger=="disconnect"&&count==262144)throw new IOException("连接已关闭");
                            output.Write(bytes,0,count);
                            if((trigger=="block"||trigger=="expiry")&&count==262144||trigger=="central"&&BitConverter.ToUInt32(bytes,0)==0x02014b50)invalid=true;
                        },check);
                        if(trigger=="expiry")Reject<ToolsHttp.HttpError>(action);else Reject<IOException>(action);
                        Check(!Eocd(output.ToArray()),"中断仍写出了完整 EOCD："+trigger);
                    }
                }
            });
            run("访客 ZIP：稳定目录 ID、授权快照、目录下载、中文路径及原 Range 兼容", () => GuestContract(root));
            run("访客目录：1000 个 32 层特殊路径保持可加载，入口不超过 2000 且子树完整", () => GuestMetadata(root));
            run("访客 ZIP：空分享/ZIP32 越界在 200 响应头前返回 409 JSON", () => GuestLimits(root));
            run("访客 ZIP：同大小同时间替换与打开后句柄离开授权路径被拒绝", () => GuestIdentity(root));
            run("访客 ZIP：源文件变化、到期、停止在写头后断流且无 EOCD", () => GuestInterrupted(root));
            run("访客 ZIP：真实 HTTP 第二个归档受限、计入连接上限、断开后释放名额", () => GuestConcurrency(root));
        }

        private sealed class Reply { internal int Code;internal string Headers;internal byte[] Body; }
        private static Dictionary<string,object> Json(Reply reply) { return (Dictionary<string,object>)new JavaScriptSerializer{MaxJsonLength=32*1024*1024}.DeserializeObject(Encoding.UTF8.GetString(reply.Body)); }
        private static void SendRequest(TcpClient client,int port,string path,string token,string extra="")
        {
            byte[] header=Encoding.ASCII.GetBytes("GET "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nConnection: close\r\nContent-Length: 0\r\n"+extra+(token==null?"":"X-FQ-Guest: "+token+"\r\n")+"\r\n");
            client.GetStream().Write(header,0,header.Length);
        }
        private static string ReadHeader(Stream stream)
        {
            using(var bytes=new MemoryStream()) {
                int last=0;while(bytes.Length<16384){int value=stream.ReadByte();if(value<0)throw new IOException("缺少 HTTP 响应头");bytes.WriteByte((byte)value);last=(last<<8)|value;if(last==0x0d0a0d0a)return Encoding.ASCII.GetString(bytes.ToArray());}
                throw new IOException("HTTP 响应头过长");
            }
        }
        private static byte[] ReadRest(Stream stream,bool interrupted=false)
        {
            using(var data=new MemoryStream()) {
                byte[] buffer=new byte[8192];
                try {int count;while((count=stream.Read(buffer,0,buffer.Length))>0)data.Write(buffer,0,count);}
                catch(IOException error){var socket=error.InnerException as System.Net.Sockets.SocketException;if(!interrupted||socket==null||socket.SocketErrorCode!=System.Net.Sockets.SocketError.ConnectionReset)throw;}
                return data.ToArray();
            }
        }
        private static Reply Request(GuestTransfer server,string path,string token,string extra="")
        {
            using(var client=new TcpClient("127.0.0.1",server.Port)) {
                client.ReceiveTimeout=10000;SendRequest(client,server.Port,path,token,extra);Stream stream=client.GetStream();string header=ReadHeader(stream);
                return new Reply{Code=Int32.Parse(header.Split(' ')[1]),Headers=header,Body=ReadRest(stream)};
            }
        }
        private static Dictionary<string,object>[] Directories(Reply reply) { return ((object[])Json(reply)["directories"]).Cast<Dictionary<string,object>>().ToArray(); }
        private static void GuestContract(string root)
        {
            string folder=Path.Combine(root,"authorized","资料 中文");Directory.CreateDirectory(Path.Combine(folder,"子目录"));
            string first=Path.Combine(folder,"子目录","特殊 +&%#😀.txt"),second=Path.Combine(folder,"空文件.txt");
            File.WriteAllBytes(first,Contents[0]);File.WriteAllBytes(second,new byte[0]);
            using(var server=new GuestTransfer(new[]{folder},Path.Combine(root,"contract-receive"),false,true)) {
                Reply state=Request(server,"/api/guest",server.Token);var dirs=Directories(state);
                Check(Directories(Request(server,"/api/guest",server.Token)).Select(dir=>dir["id"]).SequenceEqual(dirs.Select(dir=>dir["id"])),"目录 ID 在会话内不稳定");
                var capability=(Dictionary<string,object>)Json(state)["archive"];Check((bool)capability["enabled"]&&Convert.ToInt32(capability["maxEntries"])==1000&&Convert.ToInt64(capability["maxBytes"])==StoreZipStream.MaxBytes,"归档能力不符");
                File.WriteAllText(Path.Combine(folder,"未授权新文件.txt"),"不许打包");
                Reply all=Request(server,"/archive?scope=all",server.Token);
                Check(all.Code==200&&all.Headers.Contains("Content-Type: application/zip")&&all.Headers.Contains("Content-Length: "+all.Body.Length+"\r\n"),"归档类型或精确长度不符");
                Verify(all.Body,new[]{"资料 中文/子目录/特殊 +&%#😀.txt","资料 中文/空文件.txt"},new[]{Contents[0],new byte[0]});
                string id=(string)dirs.Single(dir=>(string)dir["relativePath"]=="资料 中文/子目录")["id"];
                Reply nested=Request(server,"/archive?dir="+id+"&token="+server.Token,null);
                Verify(nested.Body,new[]{"子目录/特殊 +&%#😀.txt"},new[]{Contents[0]});
                Check(Request(server,"/archive?dir=资料%20中文",server.Token).Code==404&&Request(server,"/archive?dir=..%2Foutside",server.Token).Code==404,"接受了原始目录路径");
                Check(Request(server,"/archive?scope=all&dir="+id,server.Token).Code==400&&Request(server,"/archive?scope=unknown",server.Token).Code==400,"接受了歧义范围");
                Check(Request(server,"/archive?scope=all",null).Code==403&&Request(server,"/archive?scope=all",server.Token,"Origin: http://evil.invalid\r\n").Code==403,"归档授权或 Origin 不生效");
                Check(Request(server,"/archive?scope=all",server.Token,"Range: bytes=0-5\r\n").Code==416,"归档假装支持 Range");
                var files=((object[])Json(state)["files"]).Cast<Dictionary<string,object>>().ToArray();string fileId=(string)files.Single(file=>(string)file["name"]==Path.GetFileName(first))["id"];
                Reply range=Request(server,"/file?id="+fileId,server.Token,"Range: bytes=2-7\r\n");Check(range.Code==206&&range.Body.SequenceEqual(Contents[0].Skip(2).Take(6)),"旧 Range 被改变");
            }
        }
        private static object Download(GuestTransfer server)
        {
            var map=(System.Collections.IDictionary)typeof(GuestTransfer).GetField("downloads",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(server);
            return map.Values.Cast<object>().First();
        }
        private static void GuestMetadata(string root)
        {
            string path=Path.Combine(root,"deep.txt");File.WriteAllText(path,"x");
            string nested=String.Join("/",Enumerable.Repeat(new string('\'',58),30));
            var selected=Enumerable.Range(0,1000).Select(index=>new SendBatch.Item{Path=path,Relative="r"+index.ToString("D4")+"/"+nested+"/deep.txt"}).ToArray();
            using(var server=new GuestTransfer(selected,Path.Combine(root,"deep-receive"),false,true)) {
                Reply state=Request(server,"/api/guest",server.Token);Check(state.Code==200&&state.Body.Length>2097152,"合法长清单被默认 JSON 长度阻断");
                Check(((object[])Json(state)["files"]).Length==1000&&Directories(state).Length==2000,"目录入口预算不是根与直接文件目录");
            }
            string first=Path.Combine(root,"branch-a.txt"),second=Path.Combine(root,"branch-b.txt");File.WriteAllBytes(first,new byte[]{1});File.WriteAllBytes(second,new byte[]{2});
            foreach(bool reverse in new[]{false,true}) {
                var a=new SendBatch.Item{Path=first,Relative="root/mid/branch-a.txt"};var b=new SendBatch.Item{Path=second,Relative="root/mid/leaf/branch-b.txt"};
                using(var server=new GuestTransfer(reverse?new[]{b,a}:new[]{a,b},Path.Combine(root,"branch-receive"),false,true)) {
                    var directory=Directories(Request(server,"/api/guest",server.Token)).Single(item=>(string)item["relativePath"]=="root/mid");Check(Convert.ToInt32(directory["count"])==2,"有直接文件的目录漏掉了深层授权文件");
                    Reply archive=Request(server,"/archive?dir="+directory["id"],server.Token);
                    Verify(archive.Body,reverse?new[]{"mid/leaf/branch-b.txt","mid/branch-a.txt"}:new[]{"mid/branch-a.txt","mid/leaf/branch-b.txt"},reverse?new[]{new byte[]{2},new byte[]{1}}:new[]{new byte[]{1},new byte[]{2}});
                }
            }
            using(var server=new GuestTransfer(new[]{new SendBatch.Item{Path=first,Relative="含'(+&%)目录/branch-a.txt"}},Path.Combine(root,"header-receive"),false,true)) {
                var directory=Directories(Request(server,"/api/guest",server.Token))[0];Reply archive=Request(server,"/archive?dir="+directory["id"],server.Token);
                Check(archive.Headers.Contains("%27%28%2B%26%25%29"),"归档下载名未按 RFC 5987 编码引号和括号");
                Verify(archive.Body,new[]{"含'(+&%)目录/branch-a.txt"},new[]{new byte[]{1}});
            }
        }
        private static void GuestLimits(string root)
        {
            using(var empty=new GuestTransfer(new string[0],Path.Combine(root,"empty-receive"),true,true)) {
                Reply reply=Request(empty,"/archive?scope=all",empty.Token);Check(reply.Code==409&&Json(reply).ContainsKey("error")&&!reply.Headers.Contains("application/zip"),"空清单发出 ZIP 响应头");
            }
            string path=Path.Combine(root,"limit-source.txt");File.WriteAllText(path,"x");
            using(var server=new GuestTransfer(new[]{path},Path.Combine(root,"limit-receive"),false,true)) {
                object download=Download(server);download.GetType().GetField("Size",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(download,StoreZipStream.Zip32Sentinel);
                Reply reply=Request(server,"/archive?scope=all",server.Token);Check(reply.Code==409&&(string)Json(reply)["error"]==StoreZipStream.LimitMessage&&!reply.Headers.Contains("application/zip"),"ZIP32 越界已发 200 ZIP 头或错误不明确");
            }
        }
        private static void GuestIdentity(string root)
        {
            string path=Path.Combine(root,"identity.txt"),outside=Path.Combine(root,"other.txt");File.WriteAllText(path,"original");File.WriteAllText(outside,"original");
            using(var server=new GuestTransfer(new[]{path},Path.Combine(root,"identity-receive"),false,true)) {
                MethodInfo validate=typeof(GuestTransfer).GetMethod("ValidateSource",BindingFlags.Static|BindingFlags.NonPublic);
                using(var input=File.OpenRead(outside)) {
                    try{validate.Invoke(null,new[]{Download(server),input});throw new Exception("句柄路径离开授权位置仍接受");}
                    catch(TargetInvocationException error){Check(error.InnerException is ToolsHttp.HttpError,"句柄核验错误不明确");}
                }
                DateTime modified=File.GetLastWriteTimeUtc(path);File.Move(path,path+".old");File.Copy(outside,path);File.SetLastWriteTimeUtc(path,modified);
                Reply reply=Request(server,"/archive?scope=all",server.Token);Check(reply.Code==409&&((string)Json(reply)["error"]).Contains("替换"),"同长度同修改时间的替换未检查文件 ID");
            }
        }
        private sealed class HookStream : MemoryStream
        {
            internal Action<byte[],int,int> Written;
            public override void Write(byte[] bytes,int offset,int count){base.Write(bytes,offset,count);if(Written!=null)Written(bytes,offset,count);}
        }
        private static void InvokeArchive(GuestTransfer server,ToolsHttp.Response response)
        {
            var request=new ToolsHttp.Request{Method="GET",Path="/archive",Query="scope=all",Body=new byte[0]};request.Headers.Add("X-FQ-Guest",server.Token);
            try{typeof(GuestTransfer).GetMethod("Handle",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(server,new object[]{request,response});}
            catch(TargetInvocationException error){throw error.InnerException;}
        }
        private static void GuestInterrupted(string root)
        {
            foreach(string reason in new[]{"changed","expired","stopped","disconnected"}) {
                string folder=Path.Combine(root,"interrupt-"+reason);Directory.CreateDirectory(folder);string a=Path.Combine(folder,"a.bin"),b=Path.Combine(folder,"b.bin");File.WriteAllBytes(a,new byte[524305]);File.WriteAllBytes(b,new byte[8]);
                using(var server=new GuestTransfer(new[]{a,b},Path.Combine(root,"interrupt-receive-"+reason),false,true))
                using(var output=new HookStream()) {
                    bool triggered=false,connected=true;var response=new ToolsHttp.Response(output,false,false,()=>connected);
                    output.Written=(bytes,offset,count)=>{
                        if(triggered||count<4||BitConverter.ToUInt32(bytes,offset)!=0x04034b50)return;triggered=true;
                        if(reason=="changed")File.WriteAllBytes(b,new byte[9]);
                        else if(reason=="expired")typeof(GuestTransfer).GetField("lifetimeMilliseconds",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(server,0L);
                        else if(reason=="disconnected")connected=false;
                        else server.Dispose();
                    };
                    if(reason=="disconnected")Reject<IOException>(()=>InvokeArchive(server,response));else Reject<ToolsHttp.HttpError>(()=>InvokeArchive(server,response));
                    Check(response.Started&&triggered&&!Eocd(output.ToArray()),"发头后未断流或写出 EOCD："+reason);
                    Check(!Encoding.UTF8.GetString(output.ToArray()).Contains("{\"error\""),"发头后补写了 JSON");
                }
            }
            string before=Path.Combine(root,"disconnected-before.txt");File.WriteAllText(before,"not streamed");
            using(var server=new GuestTransfer(new[]{before},Path.Combine(root,"disconnected-receive"),false,true))
            using(var output=new MemoryStream()) {
                var response=new ToolsHttp.Response(output,false,false,()=>false);
                Reject<ToolsHttp.HttpError>(()=>InvokeArchive(server,response));Check(!response.Started&&output.Length==0,"预检前已断开的连接仍发出 ZIP 头");
            }
        }
        private static void Wait(Func<bool> ready)
        {
            for(int index=0;index<1000;index++){if(ready())return;Thread.Sleep(10);}throw new Exception("归档状态未及时更新");
        }
        private static bool Busy(GuestTransfer server) { return (bool)typeof(GuestTransfer).GetField("archiveBusy",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(server); }
        private static void GuestConcurrency(string root)
        {
            string large=Path.Combine(root,"slow.bin"),small=Path.Combine(root,"small.txt");File.WriteAllBytes(large,new byte[16*1024*1024]);File.WriteAllBytes(small,new byte[]{1,2,3});
            using(var server=new GuestTransfer(new[]{new SendBatch.Item{Path=large},new SendBatch.Item{Path=small,Relative="小目录/small.txt"}},Path.Combine(root,"concurrency-receive"),false,true)) {
                string id=(string)Directories(Request(server,"/api/guest",server.Token))[0]["id"];
                using(var client=new TcpClient()) {
                    client.ReceiveBufferSize=1024;client.ReceiveTimeout=10000;client.Connect("127.0.0.1",server.Port);SendRequest(client,server.Port,"/archive?scope=all",server.Token);ReadHeader(client.GetStream());
                    Check(Busy(server),"真实 HTTP 归档没有占用会话名额");
                    object http=typeof(GuestTransfer).GetField("http",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(server);
                    object gate=http.GetType().GetField("gate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(http);
                    lock(gate)Check(((HashSet<TcpClient>)http.GetType().GetField("clients",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(http)).Count>=1,"归档没有计入 HTTP 连接上限");
                    Reply second=Request(server,"/archive?scope=all",server.Token);Check(second.Code==409&&((string)Json(second)["error"]).Contains("正在进行"),"第二个归档未被限制");
                }
                Wait(()=>!Busy(server));Reply next=Request(server,"/archive?dir="+id,server.Token);Check(next.Code==200&&Eocd(next.Body),"客户端断开后名额未释放");
                using(var client=new TcpClient("127.0.0.1",server.Port)) {
                    client.ReceiveTimeout=10000;SendRequest(client,server.Port,"/archive?scope=all",server.Token);Stream input=client.GetStream();ReadHeader(input);
                    byte[] prefix=new byte[30];int offset=0;while(offset<prefix.Length){int count=input.Read(prefix,offset,prefix.Length-offset);if(count==0)throw new IOException("归档没有开始输出");offset+=count;}
                    server.Dispose();byte[] tail=ReadRest(input,true);Check(!Eocd(prefix.Concat(tail).ToArray()),"真实停止后仍写完整 ZIP");
                }
            }
        }
        private static int Main(string[] args)
        {
            try {
                if(args[0]=="generate"){GenerateFixture(args[1]);Console.WriteLine("PASS Windows Store ZIP fixture + .NET ZipArchive CRC/SHA-256");}
                else if(args[0]=="verify"){VerifyArtifact(args[1],args[2]);Console.WriteLine("PASS Android ZIP -> .NET ZipArchive CRC/SHA-256");}
                else if(args[0]=="run") {
                    int passed=0,failed=0;RunAll((name,test)=>{try{test();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+": "+error);}},args[1]);Console.WriteLine("TOTAL "+passed+" passed, "+failed+" failed");return failed==0?0:1;
                }else throw new ArgumentException("未知 ZIP 测试模式");
                return 0;
            }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
    }
}
