using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FeiqLight
{
    public static class MediaPreview
    {
        public static bool Video(string path) { return ("|.mp4|.mkv|.avi|.mov|.webm|.wmv|.m4v|").Contains("|"+Path.GetExtension(path).ToLowerInvariant()+"|") && Path.GetExtension(path).Length > 1; }
        public static bool Supported(string path) { string ext=Path.GetExtension(path).ToLowerInvariant(); return Video(path) || (ext.Length>1 && "|.jpg|.jpeg|.png|.gif|.bmp|.webp|.tif|.tiff|".Contains("|"+ext+"|")); }
        [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ImageFactory { [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap); }
        [DllImport("shell32.dll", CharSet=CharSet.Unicode, PreserveSig=true)] private static extern int SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,[MarshalAs(UnmanagedType.Interface)] out ImageFactory factory);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        // 系统缩略图提供方负责图片/视频格式，不在 UI 线程解码或加载整个大图。
        public static Bitmap Thumbnail(string path)
        {
            if(!File.Exists(path)||!Supported(path)) return null;
            ImageFactory factory=null; IntPtr handle=IntPtr.Zero;
            try {
                Guid iid=typeof(ImageFactory).GUID;
                if(SHCreateItemFromParsingName(Path.GetFullPath(path),IntPtr.Zero,ref iid,out factory)!=0) return null;
                if(factory.GetImage(new NativeSize { Width=480,Height=320 },8,out handle)!=0||handle==IntPtr.Zero) return null;
                using(Bitmap source=Image.FromHbitmap(handle)) return new Bitmap(source);
            } catch(Exception) { return null; }
            finally { if(handle!=IntPtr.Zero) DeleteObject(handle); if(factory!=null) Marshal.ReleaseComObject(factory); }
        }
        public static void Open(Control owner,string path) { OpenFile(owner,path,false); }
        public static void OpenFile(Control owner,string path,bool choose)
        {
            try {
                if(!Path.IsPathRooted(path)||!File.Exists(path))throw new IOException("文件已移动、删除或尚未接收完成。");
                string full=Path.GetFullPath(path);
                if(choose){OpenAsInfo info=new OpenAsInfo {File=full,Flags=4};int result=SHOpenWithDialog(owner.Handle,ref info);if(result<0&&result!=unchecked((int)0x800704C7))Marshal.ThrowExceptionForHR(result);}
                else System.Diagnostics.Process.Start(DefaultOpenInfo(full));
            }catch(System.ComponentModel.Win32Exception e){if(e.NativeErrorCode==1155&&!choose){OpenFile(owner,path,true);return;}using(NoticeDialog dialog=new NoticeDialog("无法打开文件",e.Message+"\n可在文件菜单选择打开方式，或打开文件所在目录。"))dialog.ShowDialog(owner.FindForm());}
            catch(Exception e){using(NoticeDialog dialog=new NoticeDialog("无法打开文件",e.Message))dialog.ShowDialog(owner.FindForm());}
        }
        public static System.Diagnostics.ProcessStartInfo DefaultOpenInfo(string path)
        {
            if(!Path.IsPathRooted(path)||!File.Exists(path))throw new IOException("文件不存在或路径不是绝对路径。");
            return new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(path)){UseShellExecute=true,Verb="open"};
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct OpenAsInfo { [MarshalAs(UnmanagedType.LPWStr)] public string File; [MarshalAs(UnmanagedType.LPWStr)] public string Class; public int Flags; }
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SHOpenWithDialog(IntPtr parent,ref OpenAsInfo info);
        public static void Preview(Control owner,string path)
        {
            try {
                if(!File.Exists(path)||!Supported(path)) throw new IOException("媒体文件已移动、删除或格式不支持。");
                if(Video(path)) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute=true }); return; }
                using(Form form=new Form { Text=Path.GetFileName(path)+" · 滚轮缩放",Width=960,Height=720,StartPosition=FormStartPosition.CenterParent })
                using(Image original=Image.FromFile(path))
                {
                    if((long)original.Width*original.Height>100000000) throw new IOException("图片过大，请使用系统图片查看器。");
                    Panel area=new Panel { Dock=DockStyle.Fill,AutoScroll=true,BackColor=Color.FromArgb(32,32,32) };
                    PictureBox picture=new PictureBox { Image=original,SizeMode=PictureBoxSizeMode.Zoom }; area.Controls.Add(picture);form.Controls.Add(area);
                    double zoom=Math.Min(1,Math.Min(900.0/original.Width,620.0/original.Height));
                    Action resize=()=>{picture.Size=new Size(Math.Max(1,(int)(original.Width*zoom)),Math.Max(1,(int)(original.Height*zoom)));};resize();
                    picture.MouseWheel+=delegate(object sender,MouseEventArgs e){zoom=Math.Max(.05,Math.Min(5,zoom*(e.Delta>0?1.2:1/1.2)));resize();};picture.MouseEnter+=delegate{picture.Focus();};
                    form.ShowDialog(owner.FindForm());picture.Image=null;
                }
            } catch(Exception e) { using(NoticeDialog dialog=new NoticeDialog("无法预览",e.Message)) dialog.ShowDialog(owner.FindForm()); }
        }
    }
}
