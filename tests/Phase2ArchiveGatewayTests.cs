using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using FeiqLight;

namespace FeiqLight.Tests
{
    // 受控慢来源真实经过 ToolsHttp + 认证 TLS + ToolsGateway，不使用全局延长超时。
    internal static class Phase2ArchiveGatewayTests
    {
        private sealed class SlowInput : MemoryStream
        {
            internal SlowInput(byte[] bytes) : base(bytes) { }
            public override int Read(byte[] bytes,int offset,int count)
            {
                Thread.Sleep(1000);return base.Read(bytes,offset,Math.Min(count,1024));
            }
        }
        private static void Check(bool value,string message){if(!value)throw new Exception(message);}
        private static TcpClient Start(ToolsGateway gateway)
        {
            Uri url=new Uri(gateway.Url);TcpClient client=new TcpClient();client.Connect(IPAddress.Loopback,url.Port);client.ReceiveTimeout=90000;
            string request="GET /file?id=controlled&archive=store HTTP/1.1\r\nHost: 127.0.0.1:"+url.Port+"\r\nX-FQ-Gateway: "+url.Query.Substring(8)+"\r\nConnection: close\r\n\r\n";
            byte[] bytes=Encoding.ASCII.GetBytes(request);client.GetStream().Write(bytes,0,bytes.Length);return client;
        }
        private static int Main(string[] args)
        {
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);int passed=0,failed=0;
            foreach(int seconds in new[]{20,60,0})try
            {
                string profile=Path.Combine(root,"case-"+seconds+"-"+Guid.NewGuid().ToString("N"));
                using(SecureIdentity serverIdentity=new SecureIdentity(Path.Combine(profile,"server")))
                using(SecureIdentity clientIdentity=new SecureIdentity(Path.Combine(profile,"client")))
                using(ManualResetEvent entered=new ManualResetEvent(false))
                using(ManualResetEvent ended=new ManualResetEvent(false))
                {
                    const string serverId="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",clientId="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
                    serverIdentity.ConfirmPeer=clientIdentity.ConfirmPeer=(name,code)=>true;
                    serverIdentity.EnsureTrusted(clientId,clientIdentity.Fingerprint,"client");clientIdentity.EnsureTrusted(serverId,serverIdentity.Fingerprint,"server");
                    byte[] content=Enumerable.Range(0,Math.Max(seconds,20)*1024).Select(index=>(byte)index).ToArray();
                    bool completed=false;
                    using(ToolsHttp server=new ToolsHttp(0,true,(request,response)=>{
                        try{
                            response.ArchiveProgress=request.Header("X-FQ-Archive-Progress")=="1";
                            bool preparing=true;
                            StoreZipStream writer=new StoreZipStream(new[]{new StoreZipStream.Entry("目录/data.bin",content.Length,DateTime.UtcNow,()=>preparing?(Stream)new SlowInput(content):new MemoryStream(content),input=>{})});
                            writer.Prepare(()=>{response.Processing();entered.Set();});preparing=false;
                            response.Begin(200,"application/zip",writer.ContentLength,"");writer.Write(response.Write,response.CheckConnection);completed=true;
                        }finally{ended.Set();}
                    },serverIdentity))
                    using(ToolsGateway gateway=new ToolsGateway(clientIdentity,new Peer{DeviceId=serverId,Fingerprint=serverIdentity.Fingerprint,Endpoint=new IPEndPoint(IPAddress.Loopback,server.Port)},server.Port,true))
                    using(TcpClient client=Start(gateway))
                    {
                        if(seconds==0){Check(entered.WaitOne(3000),"预检未开始");Stopwatch watch=Stopwatch.StartNew();client.Close();Check(ended.WaitOne(2500)&&!completed,"取消未终止预检");Console.WriteLine("PASS 预检取消 "+watch.ElapsedMilliseconds+"ms");}
                        else using(MemoryStream bytes=new MemoryStream()){
                            client.GetStream().CopyTo(bytes);byte[] all=bytes.ToArray();string text=Encoding.ASCII.GetString(all);int start=text.IndexOf("\r\n\r\n",StringComparison.Ordinal)+4;Check(start>=4&&text.StartsWith("HTTP/1.1 200 "),"网关未返回完整归档");
                            using(MemoryStream body=new MemoryStream(all,start,all.Length-start))using(ZipArchive zip=new ZipArchive(body,ZipArchiveMode.Read))using(Stream file=zip.Entries.Single().Open())using(MemoryStream saved=new MemoryStream()){file.CopyTo(saved);Check(saved.ToArray().SequenceEqual(content),"ZIP解压内容不匹配");}
                            Check(completed,"来源未完整发送");Console.WriteLine("PASS 持续进展 "+seconds+"s 预检经网关下载解压一致");
                        }
                    }
                }
                passed++;
            }
            catch(Exception error){failed++;Console.WriteLine("FAIL "+seconds+"s "+error);}
            Console.WriteLine("RESULT "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
    }
}
