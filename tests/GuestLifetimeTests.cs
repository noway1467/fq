using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static class GuestLifetimeTests
    {
        private static void Check(bool value,string message) {if(!value)throw new Exception(message);}
        private static T Field<T>(object value,string name) {return (T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).GetValue(value);}
        private static void Click(GuestWindow window,string field) {Field<Button>(window,field).PerformClick();Application.DoEvents();}
        private static int FreePort() {var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
        private static string Get(GuestTransfer server,string token)
        {
            var request=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+server.Port+"/api/guest");request.Proxy=null;request.Timeout=3000;request.Headers["X-FQ-Guest"]=token;
            using(var response=request.GetResponse())using(var reader=new StreamReader(response.GetResponseStream()))return reader.ReadToEnd();
        }
        private static void Snapshot(Form window,string path)
        {
            window.Refresh();Application.DoEvents();
            using(var image=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(image,window.ClientRectangle);image.Save(path);}
        }
        [STAThread] public static int Main(string[] args)
        {
            try {
                Application.EnableVisualStyles();
                string root=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);Directory.CreateDirectory(output);
                var store=new LocalStore(Path.Combine(root,"settings"));
                File.WriteAllText(Path.Combine(store.Root,"settings.json"),"{\"Nickname\":\"旧设置\",\"SettingsRevision\":1}",new UTF8Encoding(false));
                Check(store.LoadSettings().GuestLifetimeMinutes==60,"旧设置缺键未默认1小时");
                foreach(int invalid in new[]{0,-1,1441,Int32.MaxValue}) {
                    File.WriteAllText(Path.Combine(store.Root,"settings.json"),"{\"Nickname\":\"旧设置\",\"GuestLifetimeMinutes\":"+invalid+"}",new UTF8Encoding(false));
                    Check(store.LoadSettings().GuestLifetimeMinutes==60,"非法持久值没有回退默认");
                }
                Console.WriteLine("PASS 旧设置缺键及非法值回退 60 分钟");
                foreach(bool dark in new[]{false,true}) {
                    Theme.Configure(dark,"blue");
                    var settings=new AppSettings{CloseToTray=false,Notifications=false,DarkMode=dark,ReceiveFolder=Path.Combine(root,"received"),GuestLifetimeMinutes=60};store.SaveSettings(settings);
                    using(var node=new LanService(settings,FreePort(),true,"guest-lifetime"))
                    using(var main=new MainForm(node,store))
                    using(var window=new GuestWindow(main,node)) {
                        main.Show();window.Show();Application.DoEvents();var input=Field<TextBox>(window,"lifetime");
                        Control defaultSave=Field<Button>(window,"saveLifetime");Check(defaultSave.Parent.ClientRectangle.Contains(defaultSave.Bounds),"默认窗口有效期按钮越界");
                        Check(input.Text=="60","UI 默认值");
                        Check(input.BorderStyle==BorderStyle.None&&input.Parent is RoundedInput,"有效期仍使用原生方框");
                        Check(input.Parent.Top==defaultSave.Top&&input.Parent.Height==defaultSave.Height,"有效期与按钮未对齐");
                        input.Focus();Application.DoEvents();Snapshot(window,Path.Combine(output,"guest-lifetime-focused-"+(dark?"dark":"light")+".png"));
                        string folder=Path.Combine(root,"目录-"+(dark?"暗":"亮"));Directory.CreateDirectory(Path.Combine(folder,"子目录"));File.WriteAllText(Path.Combine(folder,"子目录","中文 English 长文件名.txt"),"folder");
                        typeof(GuestWindow).GetMethod("AddFiles",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,new object[]{new[]{folder}});
                        Check(Field<ListBox>(window,"files").Items.Count==1,"文件夹入口未展开文件");
                        Check(Field<Button>(window,"chooseFolder").Visible,"文件夹按钮不可见");
                        foreach(string invalid in new[]{"","0","1441","-1","1.5","abc","99999999"}) {
                            input.Text=invalid;Click(window,"saveLifetime");
                            Check(store.LoadSettings().GuestLifetimeMinutes==60,"非法 UI 输入被保存");
                            Check(Field<Label>(window,"state").Text.Contains("1–1440"),"缺少范围错误提示");
                        }
                        input.Text="120";Click(window,"saveLifetime");Check(store.LoadSettings().GuestLifetimeMinutes==120,"有效期没有持久保存");
                        using(var timer=new Timer{Interval=30}) {
                            timer.Tick+=delegate {var dialog=Application.OpenForms.OfType<SettingsDialog>().FirstOrDefault();if(dialog!=null){timer.Stop();((Button)dialog.AcceptButton).PerformClick();}};
                            timer.Start();main.OpenSettings();
                        }
                        Check(node.Settings.GuestLifetimeMinutes==120&&store.LoadSettings().GuestLifetimeMinutes==120,"保存普通设置覆盖有效期");
                        Field<ToggleSwitch>(window,"upload").Checked=true;
                        Click(window,"start");var session=Field<GuestTransfer>(window,"session");
                        Check(session!=null&&session.Active,"创建链接失败");
                        Check((session.Until-DateTime.UtcNow).TotalMinutes>119.8&&(session.Until-DateTime.UtcNow).TotalMinutes<=120,"链接没有使用设置值");
                        Check(!input.Enabled&&!Field<Button>(window,"saveLifetime").Enabled,"当前会话允许修改有效期");
                        Check(Get(session,session.Token).Contains(session.Until.ToString("o")),"网页到期时间与会话不同");
                        var data=new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(Get(session,session.Token)) as System.Collections.Generic.Dictionary<string,object>;
                        Check(Convert.ToInt64(data["remainingMilliseconds"])>119*60000L,"网页没有服务端剩余时长");
                        Check(Get(session,session.Token).Contains("relativePath"),"窗口到服务端丢失目录路径");
                        try{Get(session,new string('0',64));throw new Exception("接受无效令牌");}catch(WebException e){Check(((HttpWebResponse)e.Response).StatusCode==HttpStatusCode.Forbidden,"令牌错误状态");e.Response.Close();}
                        string suffix=dark?"dark":"light";
                        Snapshot(window,Path.Combine(output,"guest-active-"+suffix+".png"));
                        window.Size=window.MinimumSize;Application.DoEvents();Control save=Field<Button>(window,"saveLifetime");
                        Check(save.Parent.ClientRectangle.Contains(save.Bounds),"最小窗口有效期按钮越界："+save.Bounds+" parent="+save.Parent.ClientRectangle+" window="+window.Size);
                        Button[] actions=new[]{"choose","chooseFolder","removeBtn","clearBtn"}.Select(name=>Field<Button>(window,name)).ToArray();
                        for(int i=0;i<actions.Length;i++){Check(actions[i].Parent.ClientRectangle.Contains(actions[i].Bounds),"文件工具栏越界");for(int j=i+1;j<actions.Length;j++)Check(!actions[i].Bounds.IntersectsWith(actions[j].Bounds),"文件工具栏重叠");}
                        Snapshot(window,Path.Combine(output,"guest-min-"+suffix+".png"));
                        window.Stop();Check(!session.Active,"主动关闭未失效");
                        input.Text="1";Click(window,"start");session=Field<GuestTransfer>(window,"session");
                        typeof(GuestTransfer).GetField("Until",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(session,DateTime.UtcNow.AddSeconds(-1));
                        Check(session.Active,"显示时刻变化不应改变实际会话有效期");
                        typeof(GuestTransfer).GetField("lifetimeMilliseconds",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(session,0L);
                        Check(!session.Active,"到期仍活跃");
                        try{Get(session,session.Token);throw new Exception("到期链接仍授权");}catch(WebException e){Check(((HttpWebResponse)e.Response).StatusCode==HttpStatusCode.Gone,"到期状态");e.Response.Close();}
                        typeof(Timer).GetMethod("OnTick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(Field<Timer>(window,"timer"),new object[]{EventArgs.Empty});
                        Check(Field<GuestTransfer>(window,"session")==null&&Field<Label>(window,"state").Text.Contains("到期"),"界面没有结束并提示到期");
                        input.Text="1440";Click(window,"saveLifetime");
                        Snapshot(window,Path.Combine(output,"guest-ended-"+suffix+".png"));
                        window.Close();main.ExitApplication();
                    }
                    Check(store.LoadSettings().GuestLifetimeMinutes==1440,"重读设置丢失有效期");
                    using(var node=new LanService(store.LoadSettings(),FreePort(),true,"guest-restart"))using(var main=new MainForm(node,store))using(var window=new GuestWindow(main,node)) {
                        Check(Field<TextBox>(window,"lifetime").Text=="1440"&&Field<GuestTransfer>(window,"session")==null,"重启未恢复设置或自动恢复链接");main.ExitApplication();
                    }
                    Console.WriteLine("PASS "+(dark?"深色":"浅色")+" UI 输入/保存其他设置/创建/授权/倒计时/到期/关闭/重启及截图");
                }
                using(var server=new GuestTransfer(new string[0],Path.Combine(root,"received"),true,true,1440))Check((server.Until-DateTime.UtcNow).TotalMinutes>1439.8,"上限时长错误");
                try{using(var server=new GuestTransfer(new string[0],Path.Combine(root,"received"),true,true,1441)){}throw new Exception("构造器允许超限");}catch(ArgumentException){}
                Console.WriteLine("PASS 24 小时上限与构造器拒绝超限\nTOTAL 4 passed, 0 failed");return 0;
            }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
    }
}
