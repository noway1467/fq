using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FeiqLight
{
    internal static class SendBatch
    {
        internal sealed class Item {internal string Path,Relative;}
        internal static List<Item> Expand(IEnumerable<string> paths, int limit = 1000)
        {
            List<Item> result=new List<Item>();
            HashSet<string> selected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string value in paths.Distinct(StringComparer.OrdinalIgnoreCase)) {
                string path=Path.GetFullPath(value);
                if(Directory.Exists(path))Walk(path,Path.GetFileName(path.TrimEnd('\\')),result,selected,limit);
                else {if(!File.Exists(path))throw new IOException("文件已不存在："+path);Add(path,null,result,selected,limit);}
            }
            if(result.Count==0)throw new IOException("所选文件夹没有可发送的文件。");return result;
        }
        private static void Walk(string path,string relative,List<Item> result,HashSet<string> selected,int limit)
        {
            if(!Protocol.SafeRelativePath(relative)||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("文件夹路径不安全或包含链接。");
            foreach(string child in Directory.EnumerateFileSystemEntries(path).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase)) {
                string next=relative+"/"+Path.GetFileName(child);if(Directory.Exists(child))Walk(child,next,result,selected,limit);else Add(child,next,result,selected,limit);
            }
        }
        private static void Add(string path,string relative,List<Item> result,HashSet<string> selected,int limit)
        {
            if(!selected.Add(path))return;
            if(result.Count>=limit)throw new IOException("单批最多 "+limit+" 个文件，可在设置中调整。");
            if(!Protocol.SafeFileName(Path.GetFileName(path))||relative!=null&&!Protocol.SafeRelativePath(relative)||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("附件路径不安全或包含链接。");
            result.Add(new Item{Path=path,Relative=relative});
        }
        internal static string Destination(string root,Attachment file)
        {
            if(file.RelativePath==null)return ReceiveStorage.Destination(root,file.Name);
            if(!Protocol.SafeRelativePath(file.RelativePath))throw new IOException("文件夹路径无效。");
            string[] parts=file.RelativePath.Split('/');string directory=ReceiveStorage.Folder(root);
            foreach(string part in parts.Take(parts.Length-1)){directory=Path.Combine(directory,part);if(File.Exists(directory))throw new IOException("同名文件阻止创建目录。");if(Directory.Exists(directory)&&(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("接收目录不能是链接。");Directory.CreateDirectory(directory);}
            return ReceiveStorage.Destination(directory,file.Name);
        }
        internal static string DestinationPath(string root, Attachment file)
        {
            if (!Protocol.SafeFileName(file.Name)) throw new IOException("不安全的文件名。");
            string directory = ReceiveStorage.Folder(root);
            if (file.RelativePath != null)
            {
                if (!Protocol.SafeRelativePath(file.RelativePath)) throw new IOException("文件夹路径无效。");
                string[] parts = file.RelativePath.Split('/');
                foreach (string part in parts.Take(parts.Length - 1)) directory = Path.Combine(directory, part);
            }
            // 清理和历史恢复只计算位置，不能为了找断点再创建空目录。
            return Path.Combine(directory, file.Name);
        }
    }
}
