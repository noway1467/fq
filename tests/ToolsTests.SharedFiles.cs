using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace FeiqLight.Tests
{
    internal static partial class ToolsTests
    {
        private static object PrivateField(object value,string name){return value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);}
        private static void SetPrivateField(object value,string name,object field){value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(value,field);}
        private static void Await(Func<bool> condition,string message)
        {
            Stopwatch wait=Stopwatch.StartNew();while(!condition()&&wait.ElapsedMilliseconds<3000)Thread.Sleep(5);Assert(condition(),message);
        }
        private static bool Eocd(byte[] bytes){return bytes.Length>=22&&BitConverter.ToUInt32(bytes,bytes.Length-22)==0x06054b50;}
        private static int HeaderLength(Reply reply){return Int32.Parse(reply.Headers.Split(new[]{"\r\n"},StringSplitOptions.None).Single(line=>line.StartsWith("Content-Length:")).Substring(15).Trim());}
        private static void VerifySharedZip(byte[] bytes,Dictionary<string,byte[]> expected)
        {
            Assert(Eocd(bytes),"缺少 ZIP 尾记录");
            Assert(BitConverter.ToUInt16(bytes,6)==0x0800&&BitConverter.ToUInt16(bytes,8)==0,"没有复用 UTF-8 Store 编码器");
            using(MemoryStream data=new MemoryStream(bytes))using(ZipArchive zip=new ZipArchive(data,ZipArchiveMode.Read,false,Encoding.UTF8))
            {
                Assert(zip.Entries.Count==expected.Count,"ZIP 条目数量错误");
                foreach(ZipArchiveEntry entry in zip.Entries)
                {
                    Assert(expected.ContainsKey(entry.FullName),"ZIP 路径错误："+entry.FullName);
                    using(Stream input=entry.Open())using(MemoryStream content=new MemoryStream()){input.CopyTo(content);Assert(content.ToArray().SequenceEqual(expected[entry.FullName]),"ZIP 内容错误："+entry.FullName);}
                }
            }
        }
        private sealed class ArchiveConnection : IDisposable
        {
            internal readonly TcpClient Client=new TcpClient();
            internal readonly Reply Reply;
            internal readonly Stream Stream;
            internal ArchiveConnection(int port,string path,string cookie)
            {
                Client.ReceiveBufferSize=65536;Client.ReceiveTimeout=15000;Client.Connect(IPAddress.Loopback,port);Stream=Client.GetStream();
                byte[] header=Encoding.ASCII.GetBytes("GET "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nCookie: "+cookie+"\r\nConnection: close\r\n\r\n");Stream.Write(header,0,header.Length);
                var bytes=new List<byte>();int last=0;
                while(last!=0x0d0a0d0a){int value=Stream.ReadByte();if(value<0)throw new EndOfStreamException("归档响应头提前关闭");bytes.Add((byte)value);last=(last<<8)|value;}
                string text=Encoding.ASCII.GetString(bytes.ToArray());Reply=new Reply {Code=Int32.Parse(text.Split(' ')[1]),Headers=text};
            }
            internal byte[] ReadBody()
            {
                using(MemoryStream body=new MemoryStream())
                {
                    byte[] buffer=new byte[262144];
                    try{int read;while((read=Stream.Read(buffer,0,buffer.Length))>0)body.Write(buffer,0,read);}
                    catch(IOException){/* 断流用例保留已收到的字节，随后仍核验长度与 EOCD。 */}
                    return body.ToArray();
                }
            }
            public void Dispose(){Stream.Dispose();Client.Close();}
        }
        private static Stream ArchiveHandle(ToolsService server,string sessionToken)
        {
            lock(PrivateField(server,"gate"))
            {
                foreach(object archive in (System.Collections.IEnumerable)PrivateField(server,"archives"))
                    if((string)PrivateField(PrivateField(archive,"Session"),"Token")==sessionToken)return (Stream)PrivateField(archive,"Input");
            }
            return null;
        }
        private static string AllCookies(Reply reply){return String.Join("; ",reply.Headers.Split(new[]{"\r\n"},StringSplitOptions.None).Where(line=>line.StartsWith("Set-Cookie: ")).Select(line=>line.Substring(12).Split(';')[0]));}
        private static void LoopbackPackage(string root,string executable)
        {
            string[] profiles={Path.Combine(root,"a"),Path.Combine(root,"b")},ids={new string('a',32),new string('b',32)};
            int firstPort=LoopbackPorts.WithTools();int[] ports={firstPort,LoopbackPorts.WithTools(firstPort+3)};
            SecureIdentity[] identities=new SecureIdentity[2];Process[] processes=new Process[2];string[] pairings=new string[2];
            try{
                for(int index=0;index<2;index++){
                    string files=Path.Combine(profiles[index],"files");Directory.CreateDirectory(files);File.WriteAllText(Path.Combine(files,"sample.txt"),"loopback "+index,new UTF8Encoding(false));
                    new LocalStore(profiles[index]).SaveSettings(new AppSettings{DeviceId=ids[index],Port=ports[index],Nickname="回环包"+index,CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(profiles[index],"received")});
                    identities[index]=new SecureIdentity(profiles[index]);identities[index].ConfirmPeer=(name,code)=>true;
                }
                for(int index=0;index<2;index++)identities[index].EnsureTrusted(ids[1-index],identities[1-index].Fingerprint,"回环对端");
                for(int index=0;index<2;index++)using(ToolsService setup=new ToolsService(ports[index]+1,true,profiles[index],identities[index])){
                    setup.Share(Path.Combine(profiles[index],"files"));Peer target=new Peer{DeviceId=ids[index],Fingerprint=identities[index].Fingerprint,Endpoint=new IPEndPoint(IPAddress.Loopback,ports[index])};
                    using(ToolsGateway gateway=new ToolsGateway(identities[1-index],target,setup.Port,true)){
                        Uri url=new Uri(gateway.Url);Reply login=Request(url.Port,"/api/login"+url.Query,"","{\"mode\":\"share\",\"code\":\""+setup.ShareCode+"\"}");Assert(login.Code==200,"测试预配对失败");pairings[index]=AllCookies(login);
                    }
                }
                for(int index=0;index<2;index++)processes[index]=Process.Start(new ProcessStartInfo(executable,"--autostart --loopback --port "+ports[index]+" --profile \""+profiles[index]+"\""){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden});
                for(int index=0;index<2;index++){
                    Peer target=new Peer{DeviceId=ids[index],Fingerprint=identities[index].Fingerprint,Endpoint=new IPEndPoint(IPAddress.Loopback,ports[index])};
                    using(ToolsGateway gateway=new ToolsGateway(identities[1-index],target,ports[index]+1,true)){
                        Uri url=new Uri(gateway.Url);bool ready=false;Stopwatch watch=Stopwatch.StartNew();
                        while(!ready&&watch.ElapsedMilliseconds<15000){Assert(!processes[index].HasExited,"正式EXE提前退出");try{ready=Request(url.Port,"/api/status"+url.Query).Code==200;}catch(IOException){}catch(SocketException){}if(!ready)Thread.Sleep(100);}
                        Assert(ready,"正式回环EXE未提供可信TLS工具服务");
                        Reply login=Request(url.Port,"/api/reconnect"+url.Query,pairings[index],"{\"mode\":\"share\"}","X-FQ-Reconnect: 1\r\n");Assert(login.Code==200,"回环网关固定证书配对恢复失败");string cookie=AllCookies(login);
                        Reply list=Request(url.Port,"/api/list?limit=100"+"&gateway="+url.Query.Substring(8),cookie);Assert(list.Code==200&&Items(list).Length==1,"回环分页失败");
                        Reply zip=Request(url.Port,"/file?id="+list.Json()["id"]+"&archive=store&gateway="+url.Query.Substring(8),cookie);Assert(zip.Code==200,"回环归档失败");VerifySharedZip(zip.Body,new Dictionary<string,byte[]>{{"sample.txt",Encoding.UTF8.GetBytes("loopback "+index)}});
                        bool plainRejected=false;try{plainRejected=Request(ports[index]+1,"/api/status").Code!=200;}catch(IOException){plainRejected=true;}catch(SocketException){plainRejected=true;}Assert(plainRejected,"正式--loopback意外接受明文工具请求");
                        Console.WriteLine("PASS 正式EXE回环 "+(1-index)+"→"+index+"：可信TLS、配对恢复、共享分页、ZIP解压与明文拒绝");
                    }
                }
            }
            finally{foreach(Process process in processes)if(process!=null){ClosePackage(process);process.Dispose();}foreach(SecureIdentity identity in identities)if(identity!=null)identity.Dispose();}
        }
        private static void RunSharedFiles(Action<string,Action> run,string root,string filter="")
        {
            if(filter.Length>0){Action<string,Action> all=run;run=(name,test)=>{if(name.Contains(filter))all(name,test);};}
            run("阶段二：目录句柄阻止祖先替换，大小写索引不合并",()=>{
                string folder=Path.Combine(root,"pinned-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);string target=folder+"-moved";
                var pin=typeof(ToolsService).GetMethod("PinDirectory",BindingFlags.NonPublic|BindingFlags.Static);
                var handles=(List<Microsoft.Win32.SafeHandles.SafeFileHandle>)pin.Invoke(null,new object[]{folder});
                try{bool refused=false;try{Directory.Move(folder,target);}catch(IOException){refused=true;}Assert(refused&&Directory.Exists(folder),"枚举目录仍可被替换");}finally{foreach(var handle in handles)handle.Dispose();}
                Directory.Move(folder,target);Assert(Directory.Exists(target),"目录句柄没有释放");
                using(ToolsService server=new ToolsService(0,true)){
                    Type type=typeof(ToolsService).GetNestedType("SharedEntry",BindingFlags.NonPublic);var id=typeof(ToolsService).GetMethod("Id",BindingFlags.Instance|BindingFlags.NonPublic);
                    object first=Activator.CreateInstance(type,true),second=Activator.CreateInstance(type,true);SetPrivateField(first,"Root",target);SetPrivateField(second,"Root",target);SetPrivateField(first,"Path",Path.Combine(target,"Report.txt"));SetPrivateField(second,"Path",Path.Combine(target,"report.txt"));
                    Assert((string)id.Invoke(server,new[]{first})!=(string)id.Invoke(server,new[]{second}),"大小写不同路径共用授权ID");
                }
            });
            run("阶段二：LRU过期可检查并刷新，移根仍失效",()=>{
                string folder=Path.Combine(root,"lru-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"a.txt"),"original");
                using(ToolsService server=new ToolsService(0,true)){
                    server.Share(folder);string cookie=Login(server,"share"),id=(string)Items(Request(server.Port,"/api/list?limit=100",cookie))[0]["id"];
                    Type type=typeof(ToolsService).GetNestedType("SharedEntry",BindingFlags.NonPublic);var remember=typeof(ToolsService).GetMethod("Id",BindingFlags.Instance|BindingFlags.NonPublic);
                    for(int index=0;index<10001;index++){object value=Activator.CreateInstance(type,true);SetPrivateField(value,"Root",folder);SetPrivateField(value,"Path",Path.Combine(folder,"virtual-"+index));remember.Invoke(server,new[]{value});}
                    Assert(Request(server.Port,"/api/entry?id="+id,cookie).Code==404,"过期入口未明确拒绝");
                    string fresh=(string)Items(Request(server.Port,"/api/list?limit=100",cookie))[0]["id"];Assert(Request(server.Port,"/api/entry?id="+fresh,cookie).Code==200&&Encoding.UTF8.GetString(Request(server.Port,"/file?id="+fresh,cookie).Body)=="original","刷新后仍不能下载");
                    server.StopSharing();Assert(Request(server.Port,"/file?id="+fresh,cookie).Code!=200,"移根后授权仍有效");
                }
            });
            run("共享分页：旧接口 409、新分页不漏不重、后页搜索和页内登记",()=>{
                string folder=Path.Combine(root,"paging-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
                Directory.CreateDirectory(Path.Combine(folder,"Alpha 目录"));Directory.CreateDirectory(Path.Combine(folder,"z目录"));
                for(int index=0;index<1105;index++)File.WriteAllBytes(Path.Combine(folder,"item-"+index.ToString("D4")+".txt"),new byte[index%7]);
                using(ToolsService server=new ToolsService(0,true))
                {
                    server.Share(folder);string cookie=Login(server,"share");
                    Assert((bool)Request(server.Port,"/api/status").Json()["listPaging"],"未宣告分页能力");
                    Assert(Request(server.Port,"/api/list",cookie).Code==409&&Request(server.Port,"/api/files",cookie).Code==409,"旧请求被截断为成功结果");
                    Reply first=Request(server.Port,"/api/files?limit=200&sort=name&order=asc",cookie);var names=new HashSet<string>();var ids=new HashSet<string>();
                    Assert(Items(first).Length==200&&((System.Collections.IDictionary)PrivateField(server,"entries")).Count==201,"给未返回页登记了 ID");
                    Reply page=first;int pages=0;
                    while(true)
                    {
                        foreach(var item in Items(page)){Assert(names.Add((string)item["name"])&&ids.Add((string)item["id"]),"分页重复条目");Assert(item.ContainsKey("mtime"),"缺少修改时间");}
                        Assert(!(bool)page.Json()["truncated"],"1107 项目录被错误截断");string cursor=(string)page.Json()["nextCursor"];
                        if(cursor==null)break;Assert(++pages<10,"游标未前进");page=Request(server.Port,"/api/files?limit=200&sort=name&order=asc&cursor="+cursor,cookie);
                    }
                    Assert(names.Count==1107,"分页漏项");Assert((bool)Items(first)[0]["dir"]&&(bool)Items(first)[1]["dir"],"目录未置前");
                    var found=Items(Request(server.Port,"/api/list?q=ITEM-1104&limit=100",cookie));Assert(found.Length==1&&(string)found[0]["name"]=="item-1104.txt","服务端搜索没有命中后页或大小写敏感");
                    Assert(Items(Request(server.Port,"/api/list?sort=name",cookie)).Length==100,"默认页大小不是 100");Assert(Items(Request(server.Port,"/api/list?limit=999",cookie)).Length==200,"未钳制到最大 200");
                }
            });
            run("共享排序：名称、大小、时间正倒序、未知值和稳定破平局",()=>{
                Type sourceType=typeof(ToolsService).GetNestedType("SharedEntry",BindingFlags.NonPublic),itemType=typeof(ToolsService).GetNestedType("ListItem",BindingFlags.NonPublic);
                Func<string,long,long?,bool,string,object> item=(name,size,mtime,directory,path)=>{
                    object source=Activator.CreateInstance(sourceType,true);SetPrivateField(source,"Root","root");SetPrivateField(source,"Path",path);
                    object value=Activator.CreateInstance(itemType,true);SetPrivateField(value,"Source",source);SetPrivateField(value,"Name",name);SetPrivateField(value,"NormalName",name.Normalize(NormalizationForm.FormC).ToUpperInvariant());SetPrivateField(value,"Size",size);SetPrivateField(value,"Modified",mtime);SetPrivateField(value,"Directory",directory);return value;
                };
                object directoryItem=item("z",0,null,true,"root/dir"),known=item("é",4,1000,false,"root/a"),unknown=item("a",-1,null,false,"root/b"),tie=item("e\u0301",4,1000,false,"root/c");
                var method=typeof(ToolsService).GetMethod("CompareItems",BindingFlags.Static|BindingFlags.NonPublic);
                Func<object,object,string,string,int> compare=(a,b,sort,order)=>(int)method.Invoke(null,new[]{a,b,sort,order});
                foreach(string order in new[]{"asc","desc"})foreach(string sort in new[]{"name","size","mtime"})Assert(compare(directoryItem,known,sort,order)<0,"目录顺序受倒序影响");
                foreach(string order in new[]{"asc","desc"})foreach(string sort in new[]{"size","mtime"})Assert(compare(known,unknown,sort,order)<0,"未知值没有固定置后");
                Assert(compare(known,unknown,"name","asc")>0&&compare(known,unknown,"name","desc")<0,"名称正倒序无效");
                Assert(compare(known,tie,"size","asc")==compare(known,tie,"size","desc"),"同值破平局随倒序抖动");
                string folder=Path.Combine(root,"mtime-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
                foreach(string name in new[]{"a","b","unknown"})File.WriteAllBytes(Path.Combine(folder,name),new byte[name=="a"?3:1]);
                File.SetLastWriteTimeUtc(Path.Combine(folder,"a"),new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc));File.SetLastWriteTimeUtc(Path.Combine(folder,"b"),new DateTime(2026,1,2,0,0,0,DateTimeKind.Utc));File.SetLastWriteTimeUtc(Path.Combine(folder,"unknown"),new DateTime(1960,1,1,0,0,0,DateTimeKind.Utc));
                using(ToolsService server=new ToolsService(0,true))
                {
                    server.Share(folder);string cookie=Login(server,"share");
                    Assert(Items(Request(server.Port,"/api/list?sort=mtime&order=desc",cookie)).Select(value=>(string)value["name"]).SequenceEqual(new[]{"b","a","unknown"}),"真实修改时间倒序或未知值错误");
                    Assert(Items(Request(server.Port,"/api/list?sort=size&order=desc",cookie)).First()["name"].Equals("a"),"真实大小排序错误");
                    Assert(Request(server.Port,"/api/files",cookie).Json().Keys.OrderBy(key=>key).SequenceEqual(new[]{"items","name","revision"}),"旧响应字段契约改变");
                }
            });
            run("共享游标：会话、目录、条件绑定、TTL、快照淘汰与 revision 拒绝",()=>{
                string folder=Path.Combine(root,"cursor-"+Guid.NewGuid().ToString("N")),second=Path.Combine(root,"cursor-other-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);Directory.CreateDirectory(second);
                File.WriteAllText(Path.Combine(folder,"a"),"a");File.WriteAllText(Path.Combine(folder,"b"),"b");
                using(ToolsService server=new ToolsService(0,true))
                {
                    server.Share(folder);string cookie=Login(server,"share"),other=Login(server,"share");Reply page=Request(server.Port,"/api/list?limit=1",cookie);string cursor=(string)page.Json()["nextCursor"];
                    Assert(System.Text.RegularExpressions.Regex.IsMatch(cursor,"^[0-9a-f]{64}$"),"游标泄露路径或不透明性不足");
                    Assert(Request(server.Port,"/api/list?limit=1&cursor="+cursor,other).Code==410,"游标跨会话使用");
                    foreach(string extra in new[]{"&q=b","&sort=size","&order=desc","&id="+page.Json()["id"]})Assert(Request(server.Port,"/api/list?limit=1&cursor="+cursor+extra,cookie).Code==409,"游标没有绑定条件 "+extra);
                    foreach(string value in new[]{"0","-1","abc"})Assert(Request(server.Port,"/api/list?limit="+value,cookie).Code==400,"非法页大小未拒绝");
                    Assert(Request(server.Port,"/api/list?sort=random",cookie).Code==400,"非法排序未拒绝");
                    var session=((System.Collections.IEnumerable)PrivateField(server,"sessions")).Cast<object>().First();var snapshots=(System.Collections.IList)PrivateField(session,"Snapshots");
                    SetPrivateField(snapshots[0],"Created",-ToolsService.ListSnapshotLifetime);Assert(Request(server.Port,"/api/list?limit=1&cursor="+cursor,cookie).Code==410,"过期游标仍可读");
                    string first=(string)Request(server.Port,"/api/list?limit=1",cookie).Json()["nextCursor"];
                    for(int index=0;index<4;index++)Request(server.Port,"/api/list?limit=1",cookie);
                    Assert(snapshots.Count==4&&Request(server.Port,"/api/list?limit=1&cursor="+first,cookie).Code==410,"快照数量无界或淘汰游标仍可读");
                    cursor=(string)Request(server.Port,"/api/list?limit=1",cookie).Json()["nextCursor"];server.AddFolders(new[]{second});
                    Assert(Request(server.Port,"/api/list?limit=1&cursor="+cursor,cookie).Code==409,"新增根后旧快照未拒绝");
                    var roots=Items(Request(server.Port,"/api/list?limit=100",cookie));string id=(string)roots.Single(value=>value["name"].Equals(Path.GetFileName(folder)))["id"];server.RemoveFolder(folder);
                    Assert(Request(server.Port,"/api/list?id="+id+"&limit=100",cookie).Code==404,"移根未清理浏览索引");
                    Assert(((System.Collections.IDictionary)PrivateField(server,"entries")).Count==((System.Collections.IDictionary)PrivateField(server,"entryIds")).Count,"反向索引未同步清理");
                }
            });
            run("共享预算：扫描硬上限、明确截断和有界反向索引",()=>{
                string folder=Path.Combine(root,"budget-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
                for(int index=0;index<=ToolsService.ListScanLimit;index++)using(File.Create(Path.Combine(folder,index.ToString("D5")))){}
                using(ToolsService server=new ToolsService(0,true))
                {
                    server.Share(folder);string cookie=Login(server,"share");Reply page=Request(server.Port,"/api/list?limit=200",cookie);Assert((bool)page.Json()["truncated"],"硬预算结果伪装为全量");
                    int count=0;var names=new HashSet<string>();
                    while(true)
                    {
                        foreach(var item in Items(page)){count++;Assert(names.Add((string)item["name"]),"截断快照翻页重复");}
                        Assert(((System.Collections.IDictionary)PrivateField(server,"entries")).Count<=10000,"浏览 ID 超过预算");
                        Assert(((System.Collections.IDictionary)PrivateField(server,"entries")).Count==((System.Collections.IDictionary)PrivateField(server,"entryIds")).Count,"反向索引泄漏");
                        string cursor=(string)page.Json()["nextCursor"];if(cursor==null)break;page=Request(server.Port,"/api/list?limit=200&cursor="+cursor,cookie);
                    }
                    Assert(count>1000&&count<=ToolsService.ListScanLimit,"扫描硬预算失效或仍限制 1000 项");
                    Assert(Items(Request(server.Port,"/api/list?limit=200&q=000",cookie)).Length>0,"索引淘汰后不能重新浏览");
                    Console.WriteLine("INFO 扫描 "+count+" 项，浏览 ID "+((System.Collections.IDictionary)PrivateField(server,"entries")).Count+" 项");
                }
            });
            run("共享 ZIP：真实目录 ID、原授权边界、UTF-8 Store、空文件和 HEAD",()=>{
                string folder=Path.Combine(root,"zip-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(folder,"子目录"));
                byte[] content=Encoding.UTF8.GetBytes("中文 English 😀 +%#");File.WriteAllBytes(Path.Combine(folder,"子目录","中文 +%#😀.txt"),content);File.WriteAllBytes(Path.Combine(folder,"empty.txt"),new byte[0]);
                using(ToolsService server=new ToolsService(0,true))
                {
                    server.Share(folder);string cookie=Login(server,"share"),id=(string)Request(server.Port,"/api/list?limit=100",cookie).Json()["id"],path="/file?id="+id+"&archive=store";
                    Assert(Request(server.Port,path).Code==401&&Request(server.Port,path,"",null,"X-FQ-Guest: fake\r\n").Code==401,"共享 ZIP 借用了访客授权");
                    Assert(Request(server.Port,path,cookie,null,"Origin: https://evil.invalid\r\n").Code==403,"归档跨站读取");
                    Assert(Request(server.Port,"/file?archive=store",cookie).Code==404&&Request(server.Port,"/file?id="+id,cookie).Code==404,"归档未要求真实目录 ID");
                    Assert(Request(server.Port,path,cookie,null,"Range: bytes=0-5\r\n").Code==416,"归档接受 Range");
                    Reply zip=Request(server.Port,path,cookie);Assert(zip.Code==200&&zip.Headers.Contains("application/zip")&&zip.Headers.Contains("Content-Disposition: attachment")&&HeaderLength(zip)==zip.Body.Length,"ZIP 响应契约错误");
                    VerifySharedZip(zip.Body,new Dictionary<string,byte[]>{{"empty.txt",new byte[0]},{"子目录/中文 +%#😀.txt",content}});
                    string directoryId=(string)Items(Request(server.Port,"/api/list?limit=100",cookie)).Single(item=>(bool)item["dir"])["id"];
                    string search=Uri.EscapeDataString("中文 +%#😀").Replace("%20","+");
                    Assert(Items(Request(server.Port,"/api/list?id="+directoryId+"&q="+search,cookie)).Length==1,"查询中的空格与加号编码不兼容 Android");
                    Reply head=Request(server.Port,path,cookie,null,"","HEAD");Assert(head.Code==200&&head.Body.Length==0&&HeaderLength(head)==zip.Body.Length,"归档 HEAD 不一致");
                }
            });
            run("共享 ZIP：文件数、目录深度与重解析点拒绝",()=>{
                string folder=Path.Combine(root,"zip-limit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
                for(int index=0;index<1001;index++)using(File.Create(Path.Combine(folder,index.ToString("D4")))){}
                using(ToolsService server=new ToolsService(0,true)){server.Share(folder);string cookie=Login(server,"share"),id=(string)Request(server.Port,"/api/list?limit=1",cookie).Json()["id"];Assert(Request(server.Port,"/file?id="+id+"&archive=store",cookie).Code==409,"ZIP 文件数上限失效");}
                string deep=Path.Combine(root,"zip-deep-"+Guid.NewGuid().ToString("N")),current=deep;Directory.CreateDirectory(deep);
                for(int index=0;index<33;index++){current=Path.Combine(current,"d");Directory.CreateDirectory(current);}
                using(ToolsService server=new ToolsService(0,true)){server.Share(deep);string cookie=Login(server,"share"),id=(string)Request(server.Port,"/api/list?limit=1",cookie).Json()["id"];Assert(Request(server.Port,"/file?id="+id+"&archive=store",cookie).Code==409,"ZIP 深度上限失效");}
                string linked=Path.Combine(root,"zip-links");Assert(Directory.Exists(linked)&&((File.GetAttributes(Path.Combine(linked,"link"))&FileAttributes.ReparsePoint)!=0),"缺少真实目录联接测试夹具");
                using(ToolsService server=new ToolsService(0,true)){server.Share(linked);string cookie=Login(server,"share"),id=(string)Request(server.Port,"/api/list?limit=1",cookie).Json()["id"];Assert(Request(server.Port,"/file?id="+id+"&archive=store",cookie).Code==403,"共享 ZIP 遍历了重解析点");}
            });
            run("共享 ZIP：移根即时关连接和句柄、不写 EOCD、另一根归档继续",()=>{
                string first=Path.Combine(root,"zip-first-"+Guid.NewGuid().ToString("N")),second=Path.Combine(root,"zip-second-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(first);Directory.CreateDirectory(second);
                using(FileStream file=File.Create(Path.Combine(first,"large.bin")))file.SetLength(16*1024*1024);using(FileStream file=File.Create(Path.Combine(second,"large.bin")))file.SetLength(16*1024*1024);
                using(ToolsService server=new ToolsService(0,true))
                {
                    server.AddFolders(new[]{first,second});string a=Login(server,"share"),b=Login(server,"share");var roots=Items(Request(server.Port,"/api/list?limit=100",a));
                    string idA=(string)roots.Single(item=>item["name"].Equals(Path.GetFileName(first)))["id"],idB=(string)roots.Single(item=>item["name"].Equals(Path.GetFileName(second)))["id"];
                    using(ArchiveConnection downloadA=new ArchiveConnection(server.Port,"/file?id="+idA+"&archive=store",a))using(ArchiveConnection downloadB=new ArchiveConnection(server.Port,"/file?id="+idB+"&archive=store",b))
                    {
                        Assert(downloadA.Reply.Code==200&&downloadB.Reply.Code==200,"归档未开始");
                        Await(()=>{Stream firstInput=ArchiveHandle(server,a.Substring(9)),secondInput=ArchiveHandle(server,b.Substring(9));return firstInput!=null&&firstInput.CanRead&&secondInput!=null&&secondInput.CanRead;},"没有活跃来源句柄");
                        Stream inputA=ArchiveHandle(server,a.Substring(9)),inputB=ArchiveHandle(server,b.Substring(9));
                        Stopwatch wait=Stopwatch.StartNew();server.RemoveFolder(first);Assert(wait.ElapsedMilliseconds<500&&!inputA.CanRead&&inputB.CanRead,"移根没有即时且按根关闭句柄");
                        byte[] interrupted=downloadA.ReadBody();Assert(interrupted.Length<HeaderLength(downloadA.Reply)&&!Eocd(interrupted),"移根后仍给出完整 ZIP");
                        byte[] complete=downloadB.ReadBody();Assert(complete.Length==HeaderLength(downloadB.Reply)&&Eocd(complete),"移根误断另一共享根");
                        Assert(Request(server.Port,"/api/list?limit=100",b).Code==200,"移根误撤销其他会话");
                    }
                }
            });
            run("共享 ZIP：源变化即时断流、不写 EOCD、停止与注销关闭句柄",()=>{
                foreach(string action in new[]{"source","stop","logout"})
                {
                    string folder=Path.Combine(root,"zip-cancel-"+action+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
                    using(FileStream file=File.Create(Path.Combine(folder,"a-large.bin")))file.SetLength(16*1024*1024);File.WriteAllText(Path.Combine(folder,"b-source.txt"),"original");
                    using(ToolsService server=new ToolsService(0,true))
                    {
                        server.Share(folder);string cookie=Login(server,"share"),id=(string)Request(server.Port,"/api/list?limit=1",cookie).Json()["id"];
                        using(ArchiveConnection download=new ArchiveConnection(server.Port,"/file?id="+id+"&archive=store",cookie))
                        {
                            Assert(download.Reply.Code==200,"归档未开始");Await(()=>{Stream current=ArchiveHandle(server,cookie.Substring(9));return current!=null&&current.CanRead;},"来源句柄未打开");Stream input=ArchiveHandle(server,cookie.Substring(9));
                            Stopwatch wait=Stopwatch.StartNew();
                            if(action=="source")File.WriteAllText(Path.Combine(folder,"b-source.txt"),"changed");else if(action=="stop")server.StopSharing();else Assert(Request(server.Port,"/api/logout",cookie,"{\"mode\":\"share\"}",TokenHeader(cookie)).Code==200,"注销失败");
                            Await(()=>!input.CanRead,"取消没有关闭来源句柄："+action);Assert(wait.ElapsedMilliseconds<1000,"取消过慢："+action);
                            byte[] partial=download.ReadBody();Assert(partial.Length<HeaderLength(download.Reply)&&!Eocd(partial),"取消后有完整 ZIP："+action);
                        }
                    }
                }
            });
            run("共享 ZIP 网关 TLS：撤信即时断流、关闭句柄且保留另一可信设备",()=>{
                const string hostId="dddddddddddddddddddddddddddddddd",clientA="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",clientB="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
                string data=Path.Combine(root,"zip-tls-"+Guid.NewGuid().ToString("N")),folder=Path.Combine(data,"files");Directory.CreateDirectory(folder);using(FileStream file=File.Create(Path.Combine(folder,"large.bin")))file.SetLength(16*1024*1024);
                using(SecureIdentity host=new SecureIdentity(Path.Combine(data,"host")))using(SecureIdentity first=new SecureIdentity(Path.Combine(data,"first")))using(SecureIdentity second=new SecureIdentity(Path.Combine(data,"second")))
                {
                    host.ConfirmPeer=first.ConfirmPeer=second.ConfirmPeer=(name,code)=>true;Assert(host.EnsureTrusted(clientA,first.Fingerprint,"A")&&host.EnsureTrusted(clientB,second.Fingerprint,"B")&&first.EnsureTrusted(hostId,host.Fingerprint,"host")&&second.EnsureTrusted(hostId,host.Fingerprint,"host"),"回环 TLS 信任失败");
                    using(ToolsService server=new ToolsService(0,true,null,host))
                    {
                        server.Share(folder);Peer peer=new Peer {DeviceId=hostId,Fingerprint=host.Fingerprint,Endpoint=new IPEndPoint(IPAddress.Loopback,server.Port)};
                        using(ToolsGateway gatewayA=new ToolsGateway(first,peer,server.Port,true))using(ToolsGateway gatewayB=new ToolsGateway(second,peer,server.Port,true))
                        {
                            Uri urlA=new Uri(gatewayA.Url),urlB=new Uri(gatewayB.Url);string loginJson="{\"mode\":\"share\",\"code\":\""+server.ShareCode+"\"}";
                            Reply loginA=Request(urlA.Port,"/api/login"+urlA.Query,"",loginJson),loginB=Request(urlB.Port,"/api/login"+urlB.Query,"",loginJson);Assert(loginA.Code==200&&loginB.Code==200,"网关共享登录失败");
                            string cookieA=AllCookies(loginA),cookieB=AllCookies(loginB),id=(string)Request(urlA.Port,"/api/list?limit=1",cookieA).Json()["id"];
                            using(ArchiveConnection a=new ArchiveConnection(urlA.Port,"/file?id="+id+"&archive=store",cookieA))using(ArchiveConnection b=new ArchiveConnection(urlB.Port,"/file?id="+id+"&archive=store",cookieB))
                            {
                                Assert(a.Reply.Code==200&&b.Reply.Code==200,"TLS 归档未开始");
                                Await(()=>{Stream aInput=ArchiveHandle(server,(string)loginA.Json()["token"]),bInput=ArchiveHandle(server,(string)loginB.Json()["token"]);return aInput!=null&&aInput.CanRead&&bInput!=null&&bInput.CanRead;},"TLS 来源句柄未打开");
                                Stream inputA=ArchiveHandle(server,(string)loginA.Json()["token"]),inputB=ArchiveHandle(server,(string)loginB.Json()["token"]);
                                Stopwatch wait=Stopwatch.StartNew();host.Forget(clientA);Assert(wait.ElapsedMilliseconds<500&&!inputA.CanRead&&inputB.CanRead,"撤信没有按设备关闭归档与句柄，耗时 "+wait.ElapsedMilliseconds+"ms，A "+inputA.CanRead+"，B "+inputB.CanRead);
                                byte[] partial=a.ReadBody();Assert(partial.Length<HeaderLength(a.Reply)&&!Eocd(partial),"撤信后仍有完整 EOCD");byte[] complete=b.ReadBody();Assert(complete.Length==HeaderLength(b.Reply)&&Eocd(complete),"撤信影响其他可信设备");
                                Assert(Request(urlB.Port,"/api/list?limit=1",cookieB).Code==200,"其他可信设备会话被撤销");
                            }
                        }
                    }
                }
            });
        }
    }
}
