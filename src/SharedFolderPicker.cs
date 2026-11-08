using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FeiqLight
{
    internal static class SharedFolderPicker
    {
        internal static string[] Show(IWin32Window owner)
        {
            IFileOpenDialog dialog = (IFileOpenDialog)new FileOpenDialog();
            IShellItemArray results = null;
            try
            {
                // 使用系统资源管理器式选择器，支持 Ctrl / Shift 多选，不退回狭小的树形窗口。
                dialog.SetOptions(0x20 | 0x40 | 0x200 | 0x800 | 0x1000 | 0x02000000);
                dialog.SetTitle("添加共享文件夹");
                dialog.SetOkButtonLabel("添加并共享");
                int result = dialog.Show(owner.Handle);
                if (result == unchecked((int)0x800704C7)) return new string[0];
                Marshal.ThrowExceptionForHR(result);
                dialog.GetResults(out results); uint count; results.GetCount(out count);
                string[] paths = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    IShellItem item; results.GetItemAt(i, out item);
                    try { item.GetDisplayName(0x80058000, out paths[i]); }
                    finally { Marshal.ReleaseComObject(item); }
                }
                return paths;
            }
            finally { if (results != null) Marshal.ReleaseComObject(results); Marshal.ReleaseComObject(dialog); }
        }
        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")] private class FileOpenDialog { }
        [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr types);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, uint position);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IShellItemArray items);
            void GetSelectedItems(out IShellItemArray items);
        }
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint type, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
        [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetPropertyStore(uint flags, ref Guid iid, out IntPtr result);
            void GetPropertyDescriptionList(ref Guid key, ref Guid iid, out IntPtr result);
            void GetAttributes(uint flags, uint mask, out uint attributes);
            void GetCount(out uint count);
            void GetItemAt(uint index, out IShellItem item);
            void EnumItems(out IntPtr items);
        }
    }
}
