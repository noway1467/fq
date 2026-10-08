using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FeiqLight;

internal static class StartupSmoke
{
    private delegate bool WindowVisitor(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor visitor, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private static bool AnyVisible(Process process)
    {
        bool visible=false;EnumWindows((window,unused)=>{uint id;GetWindowThreadProcessId(window,out id);if(id==process.Id&&IsWindowVisible(window))visible=true;return true;},IntPtr.Zero);return visible;
    }
    private static void Close(Process process)
    {
        if(process==null||process.HasExited)return;
        EnumWindows((window,unused)=>{uint id;GetWindowThreadProcessId(window,out id);if(id==process.Id){StringBuilder text=new StringBuilder(256);GetWindowText(window,text,text.Capacity);if(text.ToString()=="飞Q")PostMessage(window,0x0010,IntPtr.Zero,IntPtr.Zero);}return true;},IntPtr.Zero);
        if(!process.WaitForExit(5000))throw new IOException("测试进程未正常退出，保留隔离资料；PID="+process.Id);
    }
    private static void Require(bool condition,string message) { if(!condition)throw new Exception(message); }
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding=Encoding.UTF8;
        string executable=Path.GetFullPath(args[0]),data=Path.GetFullPath(args[1]);
        string root=Path.Combine(data,"startup-"+Guid.NewGuid().ToString("N").Substring(0,12));
        Process process=null,duplicate=null;
        try
        {
            Directory.CreateDirectory(root);
            int port;using(TcpListenerHandle probe=new TcpListenerHandle())port=probe.Port;
            LocalStore store=new LocalStore(Path.Combine(root,"资料 with spaces"));
            AppSettings settings=new AppSettings { Nickname="自启动真实烟测",CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(root,"received") };
            WindowsStartup startup=new WindowsStartup(Path.Combine(root,"startup-links"),executable,store.Root,port,true);
            startup.SaveSettings(store,settings,new AppSettings { Nickname=settings.Nickname,CloseToTray=false,Notifications=false,StartWithWindows=true,ReceiveFolder=settings.ReceiveFolder });
            process=Process.Start(new ProcessStartInfo(startup.ShortcutPath){UseShellExecute=true});
            Require(process!=null,"启动快捷方式未返回进程句柄");
            using(UdpClient socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
            {
                socket.Client.ReceiveTimeout=200;bool ready=false;Stopwatch timer=Stopwatch.StartNew();
                while(!ready&&timer.ElapsedMilliseconds<10000)
                {
                    Require(!process.HasExited,"自启动进程提前退出");
                    byte[] query=Protocol.Encode(123,"startup-smoke","loopback",Protocol.Entry|Protocol.Utf8|Protocol.CapUtf8,"烟测",null);
                    socket.Send(query,query.Length,new IPEndPoint(IPAddress.Loopback,port));
                    try { IPEndPoint remote=new IPEndPoint(IPAddress.Any,0);Packet packet;byte[] response=socket.Receive(ref remote);ready=remote.Port==port&&Protocol.TryParse(response,out packet)&&packet.Body==settings.Nickname; }
                    catch(SocketException) { Thread.Sleep(50); }
                }
                Require(ready,"快捷方式启动的 EXE 未使用隔离资料/端口回应 UDP");
                Require(!AnyVisible(process),"自启动仍显示主窗口或错误弹窗");
                duplicate=Process.Start(new ProcessStartInfo(startup.ShortcutPath){UseShellExecute=true});
                Require(duplicate!=null&&duplicate.WaitForExit(5000)&&duplicate.ExitCode==0,"重复自启动没有静默退出");
                Require(!process.HasExited,"重复启动影响了原实例");
            }
            Close(process);startup.SetEnabled(false);Require(!File.Exists(startup.ShortcutPath),"关闭后启动项仍存在");
            Console.WriteLine("STARTUP PASS: real .lnk -> packaged EXE; quoted Chinese/space profile; loopback UDP; no visible window; duplicate exits silently; normal close; own link removed.");
            return 0;
        }
        catch(Exception e){Console.WriteLine("STARTUP FAIL: "+e);return 1;}
        finally
        {
            try { Close(duplicate);Close(process); }
            finally
            {
                bool stopped=(process==null||process.HasExited)&&(duplicate==null||duplicate.HasExited);
                if(process!=null)process.Dispose();if(duplicate!=null)duplicate.Dispose();
                string absolute=Path.GetFullPath(root),prefix=data.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(stopped&&absolute.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(absolute))Directory.Delete(absolute,true);
            }
        }
    }
    private sealed class TcpListenerHandle : IDisposable
    {
        private readonly TcpListener listener=new TcpListener(IPAddress.Loopback,0);
        public int Port { get; private set; }
        public TcpListenerHandle(){listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;}
        public void Dispose(){listener.Stop();}
    }
}
