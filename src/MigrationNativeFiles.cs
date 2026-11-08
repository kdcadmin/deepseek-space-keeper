using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClearDisk {
    // Separate authority from NativeFiles: cleanup retains its original C-only boundary.
    internal static class MigrationNativeFiles {
        const uint ReadAttributes=0x80, GenericRead=0x80000000, GenericWrite=0x40000000;
        const uint OpenReparse=0x200000, BackupSemantics=0x2000000, WriteThrough=0x80000000;
        internal const FileAttributes ForbiddenAttributes=FileAttributes.ReparsePoint|FileAttributes.Offline|FileAttributes.System|
            FileAttributes.Encrypted|FileAttributes.SparseFile|FileAttributes.Compressed|(FileAttributes)0x40000|(FileAttributes)0x400000;
        [StructLayout(LayoutKind.Sequential)]
        struct Information {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            internal uint Volume, SizeHigh, SizeLow, Links, IdHigh, IdLow;
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct StreamData { internal long Length; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=296)] internal string Name; }
        [StructLayout(LayoutKind.Sequential)]
        struct SecurityAttributes { internal uint Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
        [StructLayout(LayoutKind.Sequential)] struct UnicodeString {internal ushort Length,MaximumLength; internal IntPtr Buffer;}
        [StructLayout(LayoutKind.Sequential)] struct ObjectAttributes {internal int Length; internal IntPtr RootDirectory,ObjectName; internal uint Attributes; internal IntPtr SecurityDescriptor,SecurityQualityOfService;}
        [StructLayout(LayoutKind.Sequential)] struct IoStatus {internal IntPtr Status,Information;}
        [DllImport("ntdll.dll")] static extern int NtCreateFile(out SafeFileHandle file,uint access,ref ObjectAttributes attributes,out IoStatus io,IntPtr allocation,uint fileAttributes,uint share,uint disposition,uint options,IntPtr ea,uint eaLength);
        [DllImport("ntdll.dll")] static extern uint RtlNtStatusToDosError(int status);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern bool CreateDirectory(string path,ref SecurityAttributes security);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle handle,out Information info);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder buffer,uint length,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint QueryDosDevice(string name,StringBuilder buffer,int length);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool GetVolumeNameForVolumeMountPoint(string root,StringBuilder name,int length);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool GetVolumeInformation(string root,StringBuilder label,uint labelLength,out uint serial,out uint componentLength,out uint flags,StringBuilder fs,uint fsLength);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern uint GetDriveType(string root);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFileAttributes(string path);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool GetDiskFreeSpaceEx(string path,out ulong available,out ulong total,out ulong free);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr FindFirstStreamW(string path,int level,out StreamData stream,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool FindNextStreamW(IntPtr find,out StreamData stream);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool FindClose(IntPtr find);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadFile(SafeFileHandle handle,byte[] buffer,uint count,out uint read,IntPtr overlapped);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool WriteFile(SafeFileHandle handle,byte[] buffer,uint count,out uint written,IntPtr overlapped);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool FlushFileBuffers(SafeFileHandle handle);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetFilePointerEx(SafeFileHandle handle,long position,out long actual,uint method);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,IntPtr data,uint size);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetFileTime(SafeFileHandle handle,IntPtr created,IntPtr accessed,ref System.Runtime.InteropServices.ComTypes.FILETIME written);

        internal static string Normalize(string path) {
            if(String.IsNullOrEmpty(path) || path.Length<3 || !Char.IsLetter(path[0]) || path[0]>127 || path[1]!=':' || path[2]!='\\' || path.IndexOf('/')>=0 || path.IndexOf(':',2)>=0 || path.IndexOf('\0')>=0 || path.Length>32000)
                throw new IOException("Migration requires an absolute local drive path without aliases or streams.");
            string tail=path.Substring(3);
            if(tail.Length>0 && tail.EndsWith("\\",StringComparison.Ordinal)) tail=tail.Substring(0,tail.Length-1);
            if(tail.Length>0) foreach(string part in tail.Split('\\')) ValidateComponent(part);
            return Char.ToUpperInvariant(path[0])+":\\"+tail;
        }
        internal static void ValidateComponent(string part) {
            if(String.IsNullOrEmpty(part) || part=="." || part==".." || part.Length>255 || part.EndsWith(".",StringComparison.Ordinal) || part.EndsWith(" ",StringComparison.Ordinal)) throw new IOException("Ambiguous migration path component.");
            for(int i=0;i<part.Length;i++) {
                char c=part[i];
                if(Char.IsHighSurrogate(c)) {if(i+1>=part.Length || !Char.IsLowSurrogate(part[i+1])) throw new IOException("Unpaired UTF-16 surrogate in migration path."); i++; continue;}
                if(Char.IsLowSurrogate(c)) throw new IOException("Unpaired UTF-16 surrogate in migration path.");
                if(c<32 || "<>:\"/\\|?*".IndexOf(c)>=0) throw new IOException("Unsafe migration path component.");
            }
            string stem=part.Split('.')[0].TrimEnd(' ');
            string[] reserved={"CON","PRN","AUX","NUL","CLOCK$","CONIN$","CONOUT$"};
            foreach(string name in reserved) if(stem.Equals(name,StringComparison.OrdinalIgnoreCase)) throw new IOException("DOS device name rejected.");
            if(stem.Length==4 && (stem.StartsWith("COM",StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT",StringComparison.OrdinalIgnoreCase)) && "123456789\u00b9\u00b2\u00b3".IndexOf(stem[3])>=0) throw new IOException("DOS device name rejected.");
        }
        static string LongPath(string path) { return "\\\\?\\"+Normalize(path); }
        static void Failure() { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        static SafeFileHandle Open(string path,uint access,uint share,uint creation,uint flags) {
            SafeFileHandle h=CreateFile(LongPath(path),access,share,IntPtr.Zero,creation,flags|OpenReparse|BackupSemantics,IntPtr.Zero);
            if(h.IsInvalid) { int error=Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(error); } return h;
        }
        internal static SafeFileHandle OpenStable(string path,bool data) { return Open(path,ReadAttributes|(data?GenericRead:0),1,3,0); }
        // A data/list read participates in sharing. Parent WRITE sharing is required by native rename target lookup;
        // denying DELETE still pins this directory's name. Membership is independently verified and is not frozen.
        internal static SafeFileHandle OpenDirectoryPin(string path) {return Open(path,ReadAttributes|GenericRead,3,3,0);}
        internal static SafeFileHandle OpenForRename(string path) { return Open(path,ReadAttributes|0x10000,0,3,0); }
        internal static SafeFileHandle OpenForMetadata(string path) {return Open(path,ReadAttributes|0x100,1,3,0);}
        internal static SafeFileHandle OpenForTrackedDelete(string path,bool directory) {return Open(path,ReadAttributes|0x10000|(directory?0:GenericRead),1,3,0);}
        // Release confirms absence after closing its only data handle; a new reader must not retain delete-pending data.
        internal static SafeFileHandle OpenForExclusiveTrackedDelete(string path,bool directory) {return Open(path,ReadAttributes|0x10000|(directory?0:GenericRead),0,3,0);}
        internal static void ApplySupportedMetadata(SafeFileHandle handle,MigrationIdentity source) {
            const FileAttributes supported=FileAttributes.Directory|FileAttributes.Archive|FileAttributes.ReadOnly|FileAttributes.Hidden|FileAttributes.Normal|FileAttributes.NotContentIndexed;
            if((source.Attributes&~supported)!=0) throw new IOException("Unsupported migration metadata attributes.");
            var written=new System.Runtime.InteropServices.ComTypes.FILETIME {dwLowDateTime=unchecked((int)source.LastWriteFileTime),dwHighDateTime=unchecked((int)(source.LastWriteFileTime>>32))};
            if(!SetFileTime(handle,IntPtr.Zero,IntPtr.Zero,ref written)) Failure();
            // FILE_BASIC_INFO: four LARGE_INTEGER times followed by DWORD attributes, padded to 40 bytes.
            IntPtr basic=Marshal.AllocHGlobal(40);
            try {Marshal.Copy(new byte[40],0,basic,40); Marshal.WriteInt32(basic,32,(int)(source.Attributes&~FileAttributes.Directory)); if(!SetFileInformationByHandle(handle,0,basic,40)) Failure();}
            finally {Marshal.FreeHGlobal(basic);}
        }
        internal static void DeleteTrackedHandle(SafeFileHandle handle,MigrationIdentity expected,string path) {
            if(!expected.Same(Identity(handle,path)) || (expected.Attributes&FileAttributes.ReadOnly)!=0) throw new IOException("Tracked deletion identity changed or is read-only.");
            MigrationNativeFiles.RequireOrdinary(expected,(expected.Attributes&FileAttributes.Directory)!=0); MigrationNativeFiles.RequireNoStreams(path,(expected.Attributes&FileAttributes.Directory)!=0);
            IntPtr data=Marshal.AllocHGlobal(1);
            try {Marshal.WriteByte(data,1); if(!SetFileInformationByHandle(handle,4,data,1)) Failure();}
            finally {Marshal.FreeHGlobal(data);}
        }
        internal static void RequireAbsent(string path) {
            uint attributes=GetFileAttributes(LongPath(path)); if(attributes!=UInt32.MaxValue) throw new IOException("Destination already exists.");
            int error=Marshal.GetLastWin32Error(); if(error!=2 && error!=3) throw new Win32Exception(error);
        }
        static string Final(SafeFileHandle handle,uint flags) {
            var b=new StringBuilder(32768); uint n=GetFinalPathNameByHandle(handle,b,(uint)b.Capacity,flags);
            if(n==0 || n>=b.Capacity) throw new IOException("Cannot verify final migration path."); return b.ToString();
        }
        internal static MigrationIdentity Identity(SafeFileHandle handle,string expectedPath) {
            Information i; if(!GetFileInformationByHandle(handle,out i)) Failure();
            string final=Final(handle,0); if(!final.StartsWith("\\\\?\\",StringComparison.Ordinal)) throw new IOException("Unexpected native path.");
            final=Normalize(final.Substring(4));
            string expected=Normalize(expectedPath);
            if(!final.Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new IOException("Final migration path differs from requested path. Requested='"+expected+"'; observed='"+final+"'.");
            string guidPath=Final(handle,1); int end=guidPath.IndexOf("}\\",StringComparison.Ordinal);
            if(!guidPath.StartsWith("\\\\?\\Volume{",StringComparison.OrdinalIgnoreCase) || end<0) throw new IOException("Cannot bind actual volume identity.");
            return new MigrationIdentity(guidPath.Substring(0,end+2),i.Volume,((ulong)i.IdHigh<<32)|i.IdLow,final,(FileAttributes)i.Attributes,
                ((long)(uint)i.Written.dwHighDateTime<<32)|(uint)i.Written.dwLowDateTime,checked((long)(((ulong)i.SizeHigh<<32)|i.SizeLow)),i.Links);
        }
        internal static MigrationVolume ProbeVolume(string path) {
            string root=Normalize(path).Substring(0,3); var device=new StringBuilder(32768);
            if(QueryDosDevice(root.Substring(0,2),device,device.Capacity)==0) throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot query drive mapping.");
            if(!device.ToString().StartsWith("\\Device\\HarddiskVolume",StringComparison.OrdinalIgnoreCase)) throw new IOException("SUBST, network and volume aliases are rejected.");
            if(GetDriveType(root)!=3) throw new IOException("Migration requires a fixed local disk.");
            var guid=new StringBuilder(128); if(!GetVolumeNameForVolumeMountPoint(root,guid,guid.Capacity)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot query volume GUID.");
            uint serial,component,flags; var fs=new StringBuilder(128);
            if(!GetVolumeInformation(root,null,0,out serial,out component,out flags,fs,(uint)fs.Capacity)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot query filesystem identity.");
            if(!fs.ToString().Equals("NTFS",StringComparison.OrdinalIgnoreCase)) throw new IOException("Migration requires NTFS.");
            ulong available,total,free; if(!GetDiskFreeSpaceEx(root,out available,out total,out free)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot query available volume space.");
            return new MigrationVolume(guid.ToString(),serial,available);
        }
        internal static void RequireOrdinary(MigrationIdentity identity,bool directory) {
            if((identity.Attributes & ForbiddenAttributes)!=0 || ((identity.Attributes&FileAttributes.Directory)!=0)!=directory || (!directory && identity.Links!=1))
                throw new IOException("Special, linked, encrypted, sparse, compressed, system or offline entry rejected.");
        }
        internal static void RequireNoStreams(string path,bool directory) {
            StreamData data; IntPtr find=FindFirstStreamW(LongPath(path),0,out data,0);
            if(find==new IntPtr(-1)) { if(Marshal.GetLastWin32Error()==38 && directory) return; Failure(); }
            try {
                do { if(data.Name!="::$DATA") throw new IOException("Named alternate data stream rejected."); }
                while(FindNextStreamW(find,out data));
                if(Marshal.GetLastWin32Error()!=38) Failure();
            } finally {FindClose(find);}
        }
        internal sealed class ParentLocks : IDisposable {
            readonly List<SafeFileHandle> handles=new List<SafeFileHandle>();
            internal ParentLocks(string child) {
                try {
                    string parent=Path.GetDirectoryName(Normalize(child)); if(parent==null) throw new IOException("Volume root is not an operation target.");
                    MigrationVolume volume=ProbeVolume(parent); string current=parent.Substring(0,3);
                    Pin(current,volume);
                    foreach(string part in parent.Substring(3).Split(new[]{'\\'},StringSplitOptions.RemoveEmptyEntries)) {current=Path.Combine(current,part); Pin(current,volume);}
                } catch {Dispose(); throw;}
            }
            void Pin(string path,MigrationVolume volume) {
                // A directory data/list read is required: READ_ATTRIBUTES-only opens do not enforce sharing.
                // Deny DELETE sharing so this ancestor cannot be renamed/replaced. Child membership can still change.
                // Pin ancestors, never source\\.guard: a source pin prevents its future rename.
                SafeFileHandle h=OpenDirectoryPin(path); handles.Add(h); MigrationIdentity id=Identity(h,path);
                if((id.Attributes & (FileAttributes.ReparsePoint|FileAttributes.Offline|(FileAttributes)0x40000|(FileAttributes)0x400000))!=0 || (id.Attributes&FileAttributes.Directory)==0 || !id.VolumeGuid.Equals(volume.Guid,StringComparison.OrdinalIgnoreCase) || id.VolumeSerial!=volume.Serial)
                    throw new IOException("Ancestor is a link, offline entry or volume alias.");
            }
            public void Dispose() {for(int i=handles.Count-1;i>=0;i--) handles[i].Dispose(); handles.Clear();}
        }
        internal static string Hash(SafeFileHandle source,CancellationToken token,DateTime deadline) { return Transfer(source,null,token,deadline); }
        internal static string CopyAndFlush(SafeFileHandle source,SafeFileHandle destination,CancellationToken token,DateTime deadline) { return Transfer(source,destination,token,deadline); }
        static string Transfer(SafeFileHandle source,SafeFileHandle destination,CancellationToken token,DateTime deadline) {
            long position; if(!SetFilePointerEx(source,0,out position,0)) Failure();
            using(var sha=SHA256.Create()) {
                byte[] buffer=new byte[65536]; uint read;
                while(true) {
                    token.ThrowIfCancellationRequested(); if(DateTime.UtcNow>deadline) throw new IOException("Migration work budget exhausted.");
                    if(!ReadFile(source,buffer,(uint)buffer.Length,out read,IntPtr.Zero)) Failure(); if(read==0) break;
                    if(destination!=null) {uint written; if(!WriteFile(destination,buffer,read,out written,IntPtr.Zero)) Failure(); if(written!=read) throw new IOException("Incomplete native write.");}
                    sha.TransformBlock(buffer,0,(int)read,buffer,0);
                }
                sha.TransformFinalBlock(new byte[0],0,0); if(destination!=null && !FlushFileBuffers(destination)) Failure(); return Convert.ToBase64String(sha.Hash);
            }
        }
        internal static SafeFileHandle CreateNewFile(string path) {
            return Open(path,GenericWrite|GenericRead|ReadAttributes,0,1,WriteThrough);
        }
        internal static void CreatePrivateDirectory(string path) {
            byte[] binary=PrivateSecurity(); GCHandle pin=GCHandle.Alloc(binary,GCHandleType.Pinned);
            try {var attributes=new SecurityAttributes {Length=(uint)Marshal.SizeOf(typeof(SecurityAttributes)),Descriptor=pin.AddrOfPinnedObject()}; if(!CreateDirectory(LongPath(path),ref attributes)) Failure();}
            finally {pin.Free();}
        }
        // Atomic exclusive creation returns the very object created. No pathname reopen/adoption window.
        internal static SafeFileHandle CreatePrivateDirectoryAndOpen(string path) {
            path=Normalize(path); string parent=Path.GetDirectoryName(path); if(parent==null) throw new IOException("Volume root creation is forbidden.");
            using(var pins=new ParentLocks(path))
            using(var parentHandle=OpenStable(parent,false)) {
                MigrationIdentity parentId=Identity(parentHandle,parent); RequireOrdinary(parentId,true); RequireNoStreams(parent,true);
                return CreateRelativeNew(path,parentHandle,parentId,true);
            }
        }
        internal static SafeFileHandle CreatePrivateDirectoryAndOpen(string path,SafeFileHandle parent,MigrationIdentity expectedParent) {return CreateRelativeNew(path,parent,expectedParent,true);}
        internal static SafeFileHandle CreateNewFile(string path,SafeFileHandle parent,MigrationIdentity expectedParent) {return CreateRelativeNew(path,parent,expectedParent,false);}
        static SafeFileHandle CreateRelativeNew(string path,SafeFileHandle parentHandle,MigrationIdentity expectedParent,bool directory) {
                path=Normalize(path); string parentPath=Path.GetDirectoryName(path);
                MigrationIdentity parentNow=Identity(parentHandle,parentPath);
                if(expectedParent.FileId!=parentNow.FileId || expectedParent.VolumeSerial!=parentNow.VolumeSerial || expectedParent.VolumeGuid!=parentNow.VolumeGuid || expectedParent.FinalPath!=parentNow.FinalPath) throw new IOException("Created-object parent identity changed."); RequireOrdinary(parentNow,true);
                byte[] binary=PrivateSecurity(); GCHandle descriptor=GCHandle.Alloc(binary,GCHandleType.Pinned);
                IntPtr text=Marshal.StringToHGlobalUni(Path.GetFileName(path)),unicode=IntPtr.Zero;
                try {
                    var name=new UnicodeString {Length=checked((ushort)(Path.GetFileName(path).Length*2)),MaximumLength=checked((ushort)((Path.GetFileName(path).Length+1)*2)),Buffer=text};
                    unicode=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString))); Marshal.StructureToPtr(name,unicode,false);
                    var attributes=new ObjectAttributes {Length=Marshal.SizeOf(typeof(ObjectAttributes)),RootDirectory=parentHandle.DangerousGetHandle(),ObjectName=unicode,Attributes=0x40,SecurityDescriptor=descriptor.AddrOfPinnedObject()};
                    IoStatus io; SafeFileHandle handle;
                    uint access=GenericWrite|ReadAttributes|0x100000|(directory?0x10000:GenericRead);
                    uint options=0x20|OpenReparse|(directory?1U|0x4000:0x40U|2);
                    int status=NtCreateFile(out handle,access,ref attributes,out io,IntPtr.Zero,(uint)(directory?FileAttributes.Directory:FileAttributes.Normal),0,2,options,IntPtr.Zero,0);
                    if(status<0) {if(handle!=null) handle.Dispose(); throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));}
                    try {RequireOrdinary(Identity(handle,path),directory); return handle;} catch {handle.Dispose(); throw;}
                } finally {if(unicode!=IntPtr.Zero) Marshal.FreeHGlobal(unicode); Marshal.FreeHGlobal(text); descriptor.Free();}
        }
        static byte[] PrivateSecurity() {
            var security=new DirectorySecurity(); security.SetAccessRuleProtection(true,false);
            SecurityIdentifier sid=WindowsIdentity.GetCurrent().User; security.SetOwner(sid);
            foreach(var principal in new[]{sid,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
                security.AddAccessRule(new FileSystemAccessRule(principal,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            return security.GetSecurityDescriptorBinaryForm();
        }
        internal static void RequirePrivateDirectory(string path) {
            DirectorySecurity security=Directory.GetAccessControl(path,AccessControlSections.Access|AccessControlSections.Owner);
            SecurityIdentifier sid=WindowsIdentity.GetCurrent().User;
            if(!security.AreAccessRulesProtected || !sid.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new IOException("Private directory owner or inheritance is unsafe.");
            foreach(FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier))) {
                string who=rule.IdentityReference.Value;
                if(rule.AccessControlType==AccessControlType.Allow && who!=sid.Value && who!="S-1-5-18" && who!="S-1-5-32-544") throw new IOException("Private directory grants another identity access.");
            }
        }
        // Primitive for a future capability-validating executor; no path-recursive deletion API exists here.
        internal static void RenameNoReplace(SafeFileHandle source,MigrationIdentity expected,string destination) {
            string dest=Normalize(destination);
            using(var sourceParents=new ParentLocks(expected.FinalPath))
            using(var destinationParents=new ParentLocks(dest)) {
                if(!expected.Same(Identity(source,expected.FinalPath))) throw new IOException("Rename source identity changed.");
                MigrationVolume volume=ProbeVolume(dest); if(!volume.Guid.Equals(expected.VolumeGuid,StringComparison.OrdinalIgnoreCase) || volume.Serial!=expected.VolumeSerial) throw new IOException("Native rename must remain on the same actual volume.");
                byte[] name=Encoding.Unicode.GetBytes(dest); int rootOffset=IntPtr.Size==8?8:4, lengthOffset=rootOffset+IntPtr.Size, nameOffset=lengthOffset+4;
                int size=checked(nameOffset+name.Length+2); IntPtr data=Marshal.AllocHGlobal(size);
                try {Marshal.Copy(new byte[size],0,data,size); Marshal.WriteInt32(data,lengthOffset,name.Length); Marshal.Copy(name,0,IntPtr.Add(data,nameOffset),name.Length); if(!SetFileInformationByHandle(source,3,data,(uint)size)) Failure();}
                finally {Marshal.FreeHGlobal(data);}
            }
        }
    }
}
