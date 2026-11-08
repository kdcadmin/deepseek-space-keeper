using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClearDisk {
    internal static class NativeFiles {
        const uint ReadAttributes=0x80, DeleteAccess=0x10000;
        const uint OpenReparse=0x00200000, BackupSemantics=0x02000000;
        [StructLayout(LayoutKind.Sequential)]
        internal struct Information {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IdHigh, IdLow;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle file,out Information information);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder path,uint size,uint flags);
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle file,int kind,ref Disposition data,uint size);
        [DllImport("kernel32.dll",SetLastError=true,EntryPoint="SetFileInformationByHandle")]
        static extern bool SetRenameInformation(SafeFileHandle file,int kind,IntPtr data,uint size);
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool ReadFile(SafeFileHandle file,byte[] buffer,uint requested,out uint read,IntPtr overlapped);
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool SetFilePointerEx(SafeFileHandle file,long distance,out long position,uint method);

        internal static string Normalize(string path) {
            if (String.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\",StringComparison.Ordinal) || path.IndexOf('\0')>=0)
                throw new IOException("只支持本地 C 盘普通路径。");
            for(int i=0;i<path.Length;i++) {
                if(Char.IsHighSurrogate(path[i])) {
                    if(i+1>=path.Length || !Char.IsLowSurrogate(path[i+1])) throw new IOException("跳过无法无损记录的文件名。");
                    i++;
                } else if(Char.IsLowSurrogate(path[i])) throw new IOException("跳过无法无损记录的文件名。");
            }
            string full=Path.GetFullPath(path);
            if (!full.StartsWith("C:\\",StringComparison.OrdinalIgnoreCase) || full.IndexOf(':',2)>=0)
                throw new IOException("仅处理 C 盘，不接受设备路径或数据流。");
            foreach(string part in full.Substring(3).Split('\\')) {
                if (part.EndsWith(".",StringComparison.Ordinal) || part.EndsWith(" ",StringComparison.Ordinal))
                    throw new IOException("跳过含特殊结尾的路径。");
            }
            return full.Length>3 ? full.TrimEnd('\\') : full;
        }
        static string LongPath(string path) { return "\\\\?\\"+Normalize(path); }
        internal static SafeFileHandle Open(string path,bool delete,bool directory) {
            SafeFileHandle handle=CreateFile(LongPath(path),ReadAttributes|(delete ? DeleteAccess : 0),
                directory ? 3u : (delete ? 0u : 7u),IntPtr.Zero,3,OpenReparse|BackupSemantics,IntPtr.Zero);
            if (handle.IsInvalid) { int code=Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
            return handle;
        }
        internal static SafeFileHandle OpenStable(string path) {
            SafeFileHandle handle=CreateFile(LongPath(path),ReadAttributes,1u,IntPtr.Zero,3,OpenReparse|BackupSemantics,IntPtr.Zero);
            if(handle.IsInvalid){int code=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(code);}return handle;
        }
        internal static SafeFileHandle OpenData(string path,bool delete) {
            SafeFileHandle handle=CreateFile(LongPath(path),0x80000000u|ReadAttributes|(delete?DeleteAccess:0),delete?0u:1u,IntPtr.Zero,3,OpenReparse|BackupSemantics,IntPtr.Zero);
            if(handle.IsInvalid){int code=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(code);}return handle;
        }
        static SafeFileHandle OpenDirectoryPin(string path) {
            // Share writes so NTFS can open the parent for creating/renaming children.
            // Keep DELETE sharing denied to prevent renaming the directory itself.
            SafeFileHandle handle=CreateFile(LongPath(path),0x80000000u|ReadAttributes,3u,IntPtr.Zero,3,OpenReparse|BackupSemantics,IntPtr.Zero);
            if(handle.IsInvalid){int code=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(code);}return handle;
        }
        internal static string Hash(SafeFileHandle handle,System.Threading.CancellationToken token) {
            long position;if(!SetFilePointerEx(handle,0,out position,0))throw new Win32Exception(Marshal.GetLastWin32Error());
            using(var sha=System.Security.Cryptography.SHA256.Create()){
                byte[] buffer=new byte[65536];uint read;
                while(true){token.ThrowIfCancellationRequested();if(!ReadFile(handle,buffer,(uint)buffer.Length,out read,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());if(read==0)break;sha.TransformBlock(buffer,0,(int)read,buffer,0);}
                sha.TransformFinalBlock(new byte[0],0,0);return Convert.ToBase64String(sha.Hash);
            }
        }
        internal static Information Info(SafeFileHandle handle) {
            Information info;
            if (!GetFileInformationByHandle(handle,out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return info;
        }
        static readonly object installMapGate=new object();
        static string installLink, installTarget;
        static void EnsureInstallMap() {
            if(installLink!=null) return;
            lock(installMapGate) {
                if(installLink!=null) return;
                string link="", target="";
                try {
                    link=Normalize(AppDomain.CurrentDomain.BaseDirectory);
                    SafeFileHandle handle=CreateFile(LongPath(link),0x80000000u|ReadAttributes,3u,IntPtr.Zero,3,BackupSemantics,IntPtr.Zero);
                    if(!handle.IsInvalid) {
                        var buffer=new StringBuilder(32768);
                        uint size=GetFinalPathNameByHandle(handle,buffer,(uint)buffer.Capacity,0);
                        handle.Dispose();
                        if(size>0 && size<buffer.Capacity) {
                            target=buffer.ToString();
                            if(target.StartsWith("\\\\?\\",StringComparison.Ordinal)) target=target.Substring(4);
                            target=target.TrimEnd('\\');
                        }
                    } else handle.Dispose();
                } catch(IOException) {}
                installTarget=target??"";
                installLink=link??"";
            }
        }
        static string RewriteInstallPath(string path) {
            EnsureInstallMap();
            if(installLink.Length==0 || installTarget.Length==0 || String.Equals(installLink,installTarget,StringComparison.OrdinalIgnoreCase)) return path;
            if(String.Equals(path,installTarget,StringComparison.OrdinalIgnoreCase)) return installLink;
            if(path.StartsWith(installTarget+"\\",StringComparison.OrdinalIgnoreCase)) return installLink+path.Substring(installTarget.Length);
            return path;
        }
        internal static string Final(SafeFileHandle handle) {
            var buffer=new StringBuilder(32768);
            uint size=GetFinalPathNameByHandle(handle,buffer,(uint)buffer.Capacity,0);
            if (size==0 || size>=buffer.Capacity) throw new IOException("无法核验最终路径。");
            string path=buffer.ToString();
            if (path.StartsWith("\\\\?\\",StringComparison.Ordinal)) path=path.Substring(4);
            return Normalize(RewriteInstallPath(path));
        }
        internal static FileStamp Stamp(SafeFileHandle handle) {
            Information info=Info(handle);
            return new FileStamp {Volume=info.Volume, FileId=((ulong)info.IdHigh<<32)|info.IdLow,
                Length=(long)(((ulong)info.SizeHigh<<32)|info.SizeLow), Links=info.Links,
                LastWriteTicks=DateTime.FromFileTimeUtc(((long)(uint)info.Written.dwHighDateTime<<32)|(uint)info.Written.dwLowDateTime).Ticks,
                FinalPath=Final(handle)};
        }
        internal static void Delete(SafeFileHandle handle) {
            var disposition=new Disposition {Delete=true};
            if (!SetFileInformationByHandle(handle,4,ref disposition,(uint)Marshal.SizeOf(typeof(Disposition))))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        // The open source handle is the mutation target. The destination is pinned by the caller.
        // FileRenameInfo with ReplaceIfExists=false atomically refuses an existing destination.
        internal static void Rename(SafeFileHandle handle,string destination) {
            byte[] name=Encoding.Unicode.GetBytes(Normalize(destination));
            int rootOffset=IntPtr.Size==8 ? 8 : 4;
            int lengthOffset=rootOffset+IntPtr.Size,nameOffset=lengthOffset+4;
            int size=checked(nameOffset+name.Length+2); // FileName is NUL-terminated; FileNameLength excludes that terminator.
            IntPtr buffer=Marshal.AllocHGlobal(size);
            try {
                Marshal.Copy(new byte[size],0,buffer,size);
                Marshal.WriteInt32(buffer,lengthOffset,name.Length);
                Marshal.Copy(name,0,IntPtr.Add(buffer,nameOffset),name.Length);
                if(!SetRenameInformation(handle,3,buffer,(uint)size))throw new Win32Exception(Marshal.GetLastWin32Error());
            }finally {Marshal.FreeHGlobal(buffer);}
        }
        internal sealed class ParentLocks : IDisposable {
            readonly List<SafeFileHandle> handles=new List<SafeFileHandle>();
            internal ParentLocks(string path) {
                try {
                    string parent=Path.GetDirectoryName(Normalize(path));
                    if (parent==null) throw new IOException("不操作卷根目录。");
                    string current=parent.Substring(0,3);
                    var dirs=new List<string>{current};
                    foreach(string part in parent.Substring(3).Split(new[]{'\\'},StringSplitOptions.RemoveEmptyEntries)) {
                        current=Path.Combine(current,part); dirs.Add(current);
                    }
                    foreach(string dir in dirs) {
                        // Attribute-only handles do not participate in NTFS data sharing checks.
                        // A directory data-read handle denies DELETE sharing and pins this ancestor.
                        // It still permits new children; callers must verify membership separately.
                        SafeFileHandle h=OpenDirectoryPin(dir); handles.Add(h);
                        Information info=Info(h);
                        string normalized=Normalize(dir);
                        bool installRoot=false, underInstall=false;
                        try {
                            string root=AppDomain.CurrentDomain.BaseDirectory;
                            if(!String.IsNullOrEmpty(root)) {
                                root=Normalize(root);
                                installRoot=String.Equals(normalized,root,StringComparison.OrdinalIgnoreCase);
                                underInstall=installRoot||normalized.StartsWith(root+"\\",StringComparison.OrdinalIgnoreCase);
                            }
                        } catch(IOException) {}
                        bool reparse=(info.Attributes & 0x400)!=0;
                        // The install folder itself may be a junction. Do not resolve a final path behind it. Any other link still fails.
                        if ((info.Attributes & 0x10)==0 || (reparse && !installRoot) || (!underInstall && !String.Equals(Final(h),normalized,StringComparison.OrdinalIgnoreCase)))
                            throw new IOException("目录含链接或路径已变化。");
                    }
                } catch { Dispose(); throw; }
            }
            public void Dispose() { for(int i=handles.Count-1;i>=0;i--) handles[i].Dispose(); handles.Clear(); }
        }
    }
}
