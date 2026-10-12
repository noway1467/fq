using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using FeiqLight;

// 发布前仅验证身份设计新增边界，不重复执行完整 UI/传输回归。
internal static class IdentityEdgeTests
{
    private static void Check(bool value,string text) { if(!value)throw new Exception(text); }
    private static object Field(object target,string name) { return target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target); }
    private static int Port() { var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();try{return ((IPEndPoint)listener.LocalEndpoint).Port;}finally{listener.Stop();} }
    [STAThread] public static int Main(string[] args)
    {
        string parent=Path.GetFullPath(args[0]),root=Path.Combine(parent,"identity-edge-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            using(var raw=new UdpClient(new IPEndPoint(IPAddress.Loopback,Port())))
            using(var moved=new UdpClient(new IPEndPoint(IPAddress.Loopback,Port()))) {
                var endpoint=(IPEndPoint)raw.Client.LocalEndPoint;
                var settings=new AppSettings {Notifications=false,CloseToTray=false,Conversations=new[]{new SavedConversation {Endpoint=endpoint.ToString(),Login="old",Nickname="设备",Note="备注"}}};
                using(var node=new LanService(settings,Port(),true,"local")) {
                    string key=node.Peers.Single().Id,id="0123456789abcdef0123456789abcdef";node.Start();
                    var upsert=typeof(LanService).GetMethod("Upsert",BindingFlags.Instance|BindingFlags.NonPublic);
                    Func<string,Peer> receive=device=>(Peer)upsert.Invoke(node,new object[]{new Packet {DeviceId=device,Login="old",Host="host",Body="设备",Extra="组",Command=Protocol.AnswerEntry|Protocol.Utf8},endpoint});
                    Peer peer=receive(id);Check(receive(null)==peer&&node.Peers.Count==1&&peer.Id==key&&peer.DeviceId==id&&peer.Note=="备注","旧报文覆盖身份/旧会话");
                    ChatRecord record=node.SendMessage(peer,"兼容旧 ACK",null);
                    byte[] ack=Protocol.Encode(9,"old","host",Protocol.ReceiveMessage|Protocol.Utf8,record.Packet.ToString(),null);raw.Send(ack,ack.Length,new IPEndPoint(IPAddress.Loopback,node.Port));
                    for(int i=0;i<200&&record.State!="已送达";i++)Thread.Sleep(10);Check(record.State=="已送达","旧 ACK 未确认");
                    var store=new LocalStore(root);
                    using(var window=new MainForm(node,store)) {
                        window.NetworkStarted();var search=(Control)Field(window,"search");var contacts=(ConversationList)Field(window,"contacts");
                        search.Text=endpoint.ToString();Check(contacts.Items.Count==1,"地址搜索缺少原联系人");
                        peer.Endpoint=(IPEndPoint)moved.Client.LocalEndPoint;
                        typeof(MainForm).GetMethod("RefreshPeers",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,null);
                        Check(contacts.Items.Count==0,"地址改变未刷新搜索结果");
                    }
                    Console.WriteLine("PASS: legacy identity + ACK compatibility; stable history key; endpoint search invalidation");
                }
            }
            return 0;
        } catch(Exception e) {Console.WriteLine(e);return 1;}
        finally {if(root.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))Directory.Delete(root,true);}
    }
}
