using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace FeiqLight
{
    internal static class WindowsShare
    {
        internal static void Enqueue(string root,string[] paths)
        {
            if(paths.Length==0||paths.Length>1000)throw new IOException("请选择 1–1000 个文件或文件夹。");
            string[] full=paths.Select(Path.GetFullPath).ToArray();if(full.Any(p=>!File.Exists(p)&&!Directory.Exists(p)))throw new IOException("部分分享文件已不存在。");
            string folder=Path.Combine(root,"system-share");Directory.CreateDirectory(folder);if(Directory.GetFiles(folder,"*.json").Length>=16)throw new IOException("待处理系统分享过多，请先打开飞Q处理。");
            SecureIdentity.AtomicBytes(Path.Combine(folder,Guid.NewGuid().ToString("N")+".json"),Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(full)));
        }
        internal static string[] Take(string root)
        {
            string folder=Path.Combine(root,"system-share");if(!Directory.Exists(folder))return null;
            string path=Directory.GetFiles(folder,"*.json").OrderBy(File.GetCreationTimeUtc).FirstOrDefault();if(path==null)return null;
            try{if(new FileInfo(path).Length>1024*1024)throw new IOException("分享列表过大。");string[] files=new JavaScriptSerializer().Deserialize<string[]>(File.ReadAllText(path,Encoding.UTF8));if(files==null||files.Length>1000||files.Any(p=>!Path.IsPathRooted(p)))throw new IOException("分享列表无效。");return files;}
            finally{File.Delete(path);}
        }
        internal static void Install(string folder,string executable,string root,int port)
        {
            string path=Path.Combine(folder,"飞Q.lnk");object shell=null,link=null;
            try {
                shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));link=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path});
                if(File.Exists(path)&&!Object.Equals(link.GetType().InvokeMember("Description",BindingFlags.GetProperty,null,link,null),"FeiqLight system share"))throw new IOException("同名快捷方式不属于飞Q，未覆盖。");
                Action<string,string> set=(name,value)=>link.GetType().InvokeMember(name,BindingFlags.SetProperty,null,link,new object[]{value});
                set("TargetPath",Path.GetFullPath(executable));set("Arguments","--profile \""+Path.GetFullPath(root).TrimEnd('\\')+"\" --port "+port+" --send");set("Description","FeiqLight system share");link.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);
            }finally{if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}
        }
    }
}
