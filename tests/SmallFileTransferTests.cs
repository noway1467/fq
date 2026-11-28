using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FeiqLight;

namespace FeiqLight.Tests
{
    public static class SmallFileTransferTests
    {
        private const string AId="11111111111111111111111111111111",BId="22222222222222222222222222222222";
        private static int passed,failed;
        private static object Field(object value,string name) { return value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(value); }
        private static void Check(bool value,string reason) { if(!value)throw new Exception(reason); }
        private static void Wait(Func<bool> condition,string reason)
        {
            Stopwatch timing=Stopwatch.StartNew();
            while(!condition()) {if(timing.ElapsedMilliseconds>10000)throw new Exception(reason);Thread.Sleep(5);}
        }
        private static LanService Node(string root,string id,bool start=true)
        {
            var node=new LanService(new AppSettings{DeviceId=id,ReceiveFolder=root},LoopbackPorts.Chat(),true,"wp5-"+id.Substring(0,2));
            try {node.ConfigureSecurity(root,(name,code)=>true);if(start)node.Start();return node;}catch{node.Dispose();throw;}
        }
        private static Peer Find(LanService from,LanService to)
        {
            from.Probe(new IPEndPoint(IPAddress.Loopback,to.Port));
            Wait(()=>from.Peers.Any(peer=>peer.DeviceId==to.Settings.DeviceId),"发现超时");
            return from.Peers.Single(peer=>peer.DeviceId==to.Settings.DeviceId);
        }
        private sealed class Pair : IDisposable
        {
            internal readonly LanService A,B;
            internal readonly Peer Target,Back;
            internal readonly BlockingCollection<IncomingMessage> Incoming=new BlockingCollection<IncomingMessage>();
            internal readonly string Source,Root;
            internal Pair(string root)
            {
                Root=root;Directory.CreateDirectory(root);Source=Path.Combine(root,"source.bin");File.WriteAllBytes(Source,new byte[]{3,5,7,11});
                A=Node(Path.Combine(root,"a"),AId);B=Node(Path.Combine(root,"b"),BId);B.MessageReceived+=Incoming.Add;
                Target=Find(A,B);A.VerifyPeer(Target);Back=Find(B,A);
            }
            internal List<Tuple<long,Attachment>> Send(int count)
            {
                var records=new List<ChatRecord>();var files=new List<Tuple<long,Attachment>>();
                for(int offset=0;offset<count;offset+=20)records.Add(A.SendMessage(Target,"",Enumerable.Repeat(Source,Math.Min(20,count-offset)).ToArray()));
                for(int index=0;index<records.Count;index++)
                {
                    IncomingMessage message;Check(Incoming.TryTake(out message,10000),"邀请没有到达");
                    files.AddRange(message.Files.Select(file=>Tuple.Create(message.Record.Packet,file)));
                }
                Wait(()=>records.All(record=>record.State=="已送达"),"邀请未确认送达");
                return files;
            }
            public void Dispose() {B.Dispose();A.Dispose();Incoming.Dispose();}
        }
        private static void Capacity(LanService node,int count)
        {
            try {typeof(LanService).GetMethod("CheckOfferCapacity",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(node,new object[]{count,null});}
            catch(TargetInvocationException error){throw error.InnerException;}
        }
        private static int Offers(LanService node) {lock(Field(node,"gate"))return ((IDictionary)Field(node,"offers")).Count;}
        private static int Completed(LanService node)
        {
            lock(Field(node,"gate"))return ((IDictionary)Field(node,"offers")).Values.Cast<object>().Count(offer=>(bool)Field(offer,"Stored"));
        }
        private static void Run(string name,Action test)
        {
            try {test();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+"："+error);}
        }
        public static int Main(string[] args)
        {
            Console.OutputEncoding=new UTF8Encoding(false);string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            if(args.Length>1&&args[1]=="crash-writer") {CrashWriter(root);return 1;}
            string selected=args.Length>1?args[1]:"all";
            if(selected=="all"||selected=="batch")Run("独立能力、64件连接上限、逐件授权与撤信立即断连",()=>Batch(Path.Combine(root,"batch")));
            if(selected=="all"||selected=="boundary")Run("批量连接拒绝非F下一帧",()=>Boundary(Path.Combine(root,"boundary")));
            if(selected=="all"||selected=="recovery")Run("真实进程崩溃、截断确认日志恢复与损坏日志拒绝",()=>Recovery(Path.Combine(root,"recovery")));
            if(selected=="all"||selected=="eviction")Run("1105完成项淘汰与100待处理额度跨重启",()=>Eviction(Path.Combine(root,"eviction")));
            Console.WriteLine("WINDOWS SMALL FILE: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
        private static void Batch(string root)
        {
            Packet parsed;byte[] hello=Protocol.Encode(0,"test","host",Protocol.Entry|Protocol.Utf8,"","",AId,null,new string('a',64));
            Check(Protocol.TryParse(hello.Concat(Encoding.ASCII.GetBytes("FQREV=1\0")).ToArray(),out parsed)&&parsed.ReverseConnections&&!parsed.BatchFiles,"FQREV 被错误复用为批量能力");
            Check(Protocol.TryParse(hello.Concat(Encoding.ASCII.GetBytes("FQBATCH=1\0")).ToArray(),out parsed)&&parsed.BatchFiles&&!parsed.ReverseConnections,"独立批量能力解析错误");
            using(var pair=new Pair(root))
            {
                var files=pair.Send(80);var clients=new HashSet<object>();string received=Path.Combine(root,"received");Directory.CreateDirectory(received);
                for(int index=0;index<files.Count;index++)
                {
                    var item=files[index];pair.B.ReceiveFileAsync(pair.Back,item.Item1,item.Item2,Path.Combine(received,index+".bin"),null,CancellationToken.None).GetAwaiter().GetResult();
                    object channel=Field(pair.B,"idleFileConnection");if(channel!=null)clients.Add(Field(channel,"Client"));
                }
                Check(clients.Count==2,"80件没有按64件上限复用为两条连接");
                var legacy=pair.Send(1)[0];object legacyOffer;
                lock(Field(pair.A,"gate")) {
                    legacyOffer=((IDictionary)Field(pair.A,"offers"))[legacy.Item1+":"+legacy.Item2.Id];
                    legacyOffer.GetType().GetField("TransferState").SetValue(legacyOffer,"已发送（非保存确认）");
                    legacyOffer.GetType().GetField("Percent").SetValue(legacyOffer,100);
                }
                legacy.Item2.Sha256=new string('0',64);
                try{pair.B.ReceiveFileAsync(pair.Back,legacy.Item1,legacy.Item2,Path.Combine(root,"legacy-failed.bin"),null,CancellationToken.None).GetAwaiter().GetResult();throw new Exception("错误摘要未被拒绝");}catch(IOException){}
                Wait(()=>!pair.A.HasActiveOutgoingTransfer(pair.Target.Id,legacy.Item1),"错误摘要连接没有释放");
                Check((string)Field(legacyOffer,"TransferState")=="已发送（非保存确认）","历史完成项在失败重收后退回待处理");
                pair.A.Settings.PendingFileLimit=100;Capacity(pair.A,100);
                using(LanService other=Node(Path.Combine(root,"c"),"33333333333333333333333333333333"))
                {
                    Peer sender=Find(other,pair.A);other.VerifyPeer(sender);bool denied=false;
                    try{other.ReceiveFileAsync(sender,files[0].Item1,files[0].Item2,Path.Combine(root,"unauthorized.bin"),null,CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){denied=true;}
                    Check(denied&&!File.Exists(Path.Combine(root,"unauthorized.bin")),"其他已信任设备取得了未授权附件");
                }
                var fresh=pair.Send(1)[0];
                pair.B.ReceiveFileAsync(pair.Back,fresh.Item1,fresh.Item2,Path.Combine(root,"fresh.bin"),null,CancellationToken.None).GetAwaiter().GetResult();
                var batchClients=(IDictionary)Field(pair.A,"fileBatchPeers");TcpClient active;
                lock(Field(pair.A,"gate"))active=batchClients.Keys.Cast<TcpClient>().First();
                Check(active.Connected,"撤信前批量连接已经关闭");
                pair.A.Security.ConfirmPeer=(name,code)=>false;
                Stopwatch timing=Stopwatch.StartNew();pair.A.Security.Forget(BId);
                Check(!active.Connected&&timing.ElapsedMilliseconds<1000,"撤信未立即关闭批量连接");
                Check(Directory.GetFiles(received).Length==80&&Completed(pair.A)==81,"完成文件被改写或终态退回待处理");
            }
        }
        private static void Boundary(string root)
        {
            using(var pair=new Pair(root))
            {
                var file=pair.Send(1)[0];int injected=0;pair.A.MessageReceived+=message=>Interlocked.Increment(ref injected);
                using(var client=new TcpClient())
                {
                    client.Connect(IPAddress.Loopback,pair.A.Port);client.ReceiveTimeout=2000;client.GetStream().Write(new byte[]{70,81,50,10},0,4);
                    using(SslStream tls=pair.B.Security.Client(client,pair.A.Security.Fingerprint))
                    {
                        byte[] hello=Protocol.Encode(0,pair.B.Login,pair.B.Host,Protocol.Entry|Protocol.Utf8,"","",BId,null,pair.B.Security.Fingerprint);
                        SecureIdentity.WriteFrame(tls,hello.Concat(Encoding.ASCII.GetBytes("FQBATCH=1\0")).ToArray());SecureIdentity.ReadFrame(tls,4096);
                        SecureIdentity.WriteText(tls,"YES");Check(SecureIdentity.ReadText(tls,16)=="YES","认证失败");
                        SecureIdentity.WriteText(tls,"F\n"+pair.B.Port);
                        SecureIdentity.WriteFrame(tls,Protocol.Encode(1,pair.B.Login,pair.B.Host,Protocol.GetFile|Protocol.Utf8,file.Item1.ToString("x")+":"+file.Item2.Id.ToString("x")+":0:",null,BId,null,pair.B.Security.Fingerprint));
                        Check(SecureIdentity.ReadText(tls,256).StartsWith("OK\n",StringComparison.Ordinal),"授权请求失败");
                        Check(SecureIdentity.Hash(SecureIdentity.ReadBytes(tls,(int)file.Item2.Size))==file.Item2.Sha256,"正文损坏");
                        SecureIdentity.WriteText(tls,"STORED\n"+file.Item2.Sha256);Check(SecureIdentity.ReadText(tls,16)=="NEXT","未确认批量边界");
                        SecureIdentity.WriteText(tls,"M\n"+pair.B.Port);
                        SecureIdentity.WriteFrame(tls,Protocol.Encode(2,pair.B.Login,pair.B.Host,Protocol.SendMessage|Protocol.Utf8,"invalid-batch-message",null,BId,null,pair.B.Security.Fingerprint));
                        bool rejected=false;try{SecureIdentity.ReadText(tls,16);}catch(IOException){rejected=true;}
                        Check(rejected&&injected==0,"批量通道接受了非F帧或保持了异常连接");
                    }
                }
            }
        }
        private static void CrashWriter(string root)
        {
            var pair=new Pair(root);var files=pair.Send(100);
            pair.A.TransferChanged+=(record,id,percent,state,rate)=>{
                if(state!="对方已校验保存")return;
                Console.WriteLine("CRASH_READY");Console.Out.Flush();new ManualResetEventSlim(false).Wait();
            };
            pair.B.ReceiveFileAsync(pair.Back,files[0].Item1,files[0].Item2,Path.Combine(root,"saved.bin"),null,CancellationToken.None).GetAwaiter().GetResult();
            throw new Exception("崩溃写入夹具不应正常返回");
        }
        private static void Recovery(string root)
        {
            Directory.CreateDirectory(root);string executable=Assembly.GetExecutingAssembly().Location;
            var info=new ProcessStartInfo(executable,"\""+root+"\" crash-writer"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(Process child=Process.Start(info))
            {
                try
                {
                    Task<string> ready=Task.Run(()=>child.StandardOutput.ReadLine());Check(ready.Wait(30000)&&ready.Result=="CRASH_READY","崩溃夹具未到确认日志边界");
                    Check(!child.HasExited&&String.Equals(child.MainModule.FileName,executable,StringComparison.OrdinalIgnoreCase),"子进程归属错误");
                    child.Kill();Check(child.WaitForExit(10000),"崩溃进程没有退出");
                }
                finally {if(!child.HasExited&&String.Equals(child.MainModule.FileName,executable,StringComparison.OrdinalIgnoreCase)){child.Kill();child.WaitForExit(10000);}}
            }
            string journal=Path.Combine(root,"a/transfer-offer-completions.log");Check(File.ReadAllBytes(journal).Length>0,"崩溃前没有同步确认日志");
            string[] confirmation=File.ReadLines(journal).First().Split(':');
            string snapshot=Path.Combine(root,"a/transfer-offers.json");
            var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();
            LanService.StoredOffer[] saved=serializer.Deserialize<LanService.StoredOffer[]>(File.ReadAllText(snapshot));
            saved.Single(item=>item.Key==confirmation[0]+":"+confirmation[1]).ExpiresTicks=DateTime.UtcNow.AddSeconds(-5).Ticks;
            SecureIdentity.AtomicBytes(snapshot,Encoding.UTF8.GetBytes(serializer.Serialize(saved)));
            File.AppendAllText(journal,"999:0:partial",new UTF8Encoding(false));
            using(LanService restored=Node(Path.Combine(root,"a"),AId,false))
            {
                Check(Offers(restored)==100&&Completed(restored)==1,"确认日志、续期或末尾半条恢复错误");
                restored.Settings.PendingFileLimit=100;Capacity(restored,1);bool full=false;try{Capacity(restored,2);}catch(InvalidOperationException){full=true;}
                Check(full,"崩溃恢复后已完成项重占额度或待处理额度失效");
            }
            Check(File.ReadAllBytes(Path.Combine(root,"saved.bin")).SequenceEqual(new byte[]{3,5,7,11}),"崩溃恢复损坏了单份正式文件");
            File.WriteAllText(journal,"broken-record\n",new UTF8Encoding(false));bool corrupt=false;
            try{using(Node(Path.Combine(root,"a"),AId,false)){} }catch(IOException){corrupt=true;}
            Check(corrupt&&File.ReadAllText(journal)=="broken-record\n","完整损坏记录被静默忽略或清空");
        }
        private static void Eviction(string root)
        {
            using(var pair=new Pair(root))
            {
                var files=pair.Send(1105);string received=Path.Combine(root,"r");Directory.CreateDirectory(received);
                for(int index=0;index<files.Count;index++)pair.B.ReceiveFileAsync(pair.Back,files[index].Item1,files[index].Item2,Path.Combine(received,index+".bin"),null,CancellationToken.None).GetAwaiter().GetResult();
                Check(Completed(pair.A)==1105,"1105件没有全部确认保存");
                pair.A.Settings.PendingFileLimit=100;Capacity(pair.A,100);Check(Offers(pair.A)==1000,"完成缓存未淘汰到1000项");
                pair.Send(100);bool full=false;try{Capacity(pair.A,1);}catch(InvalidOperationException){full=true;}
                Check(full&&Offers(pair.A)==1100&&Directory.GetFiles(received).Length==1105,"完成缓存占了额度或文件出现副本");
            }
            using(LanService restored=Node(Path.Combine(root,"a"),AId,false))
            {
                Check(Offers(restored)==1100&&Completed(restored)==1000,"淘汰后的完成/待处理状态未跨重启保留");
                restored.Settings.PendingFileLimit=100;bool full=false;try{Capacity(restored,1);}catch(InvalidOperationException){full=true;}Check(full,"重启后待处理额度失效");
            }
        }
    }
}
