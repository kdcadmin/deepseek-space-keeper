using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClearDisk {
    // Low-level primitives only. The executor/recovery must supply independently validated authority.
    internal static class MigrationNativeJunction {
        const uint MountPointTag=0xA0000003,SetReparse=0x900A4,GetReparse=0x900A8;
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool DeviceIoControl(SafeFileHandle file,uint code,byte[] input,uint inputSize,byte[] output,uint outputSize,out uint returned,IntPtr overlapped);
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle file,int kind,byte[] data,uint size);
        internal static MigrationIdentity Create(string path,string target) {
            path=MigrationNativeFiles.Normalize(path); target=MigrationNativeFiles.Normalize(target);
            if(path.Length<=3 || target.Length<=3 || MigrationPolicy.Within(path,target) || MigrationPolicy.Within(target,path)) throw new IOException("Unsafe junction endpoints.");
            using(var parents=new MigrationNativeFiles.ParentLocks(path))
            using(var targetParents=new MigrationNativeFiles.ParentLocks(Path.Combine(target,".junction-target-pin")))
            using(var targetHandle=MigrationNativeFiles.OpenStable(target,false)) {
                MigrationNativeFiles.RequireOrdinary(MigrationNativeFiles.Identity(targetHandle,target),true);
                MigrationNativeFiles.RequireNoStreams(target,true);
                using(var handle=MigrationNativeFiles.CreatePrivateDirectoryAndOpen(path)) {
                    string substitute="\\??\\"+target;
                    byte[] sub=Encoding.Unicode.GetBytes(substitute),print=Encoding.Unicode.GetBytes(target);
                    int dataLength=checked(8+sub.Length+2+print.Length+2);
                    if(dataLength+8>16384) throw new IOException("Junction reparse buffer exceeds the native limit.");
                    byte[] buffer=new byte[dataLength+8];
                    Put32(buffer,0,MountPointTag); Put16(buffer,4,dataLength);
                    Put16(buffer,8,0); Put16(buffer,10,sub.Length); Put16(buffer,12,sub.Length+2); Put16(buffer,14,print.Length);
                    Buffer.BlockCopy(sub,0,buffer,16,sub.Length); Buffer.BlockCopy(print,0,buffer,18+sub.Length,print.Length);
                    uint returned;
                    if(!DeviceIoControl(handle,SetReparse,buffer,(uint)buffer.Length,null,0,out returned,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    MigrationIdentity id=MigrationNativeFiles.Identity(handle,path); Verify(handle,path,id,target); return id;
                }
            }
        }
        internal static void Verify(SafeFileHandle handle,string path,MigrationIdentity expected,string target) {
            if(expected==null || !expected.Same(MigrationNativeFiles.Identity(handle,path)) || (expected.Attributes&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=(FileAttributes.ReparsePoint|FileAttributes.Directory)) throw new IOException("Junction identity mismatch.");
            MigrationNativeFiles.RequireNoStreams(path,true);
            byte[] buffer=new byte[16384]; uint returned;
            if(!DeviceIoControl(handle,GetReparse,null,0,buffer,(uint)buffer.Length,out returned,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if(returned<16 || BitConverter.ToUInt32(buffer,0)!=MountPointTag || BitConverter.ToUInt16(buffer,4)+8!=returned || BitConverter.ToUInt16(buffer,6)!=0) throw new IOException("Only the exact mount-point tag is supported.");
            string sub=ReadName(buffer,returned,8),print=ReadName(buffer,returned,12),normalized=MigrationNativeFiles.Normalize(target);
            if(sub!="\\??\\"+normalized || print!=normalized) throw new IOException("Junction target differs from transaction binding.");
        }
        internal static void Delete(SafeFileHandle handle,string path,MigrationIdentity expected,string target) {
            Verify(handle,path,expected,target);
            // FileDispositionInfo on the OPEN_REPARSE_POINT handle removes only this link, never its target.
            if(!SetFileInformationByHandle(handle,4,new byte[]{1},1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        static string ReadName(byte[] data,uint returned,int offset) {
            int start=16+BitConverter.ToUInt16(data,offset),length=BitConverter.ToUInt16(data,offset+2);
            if((start&1)!=0 || (length&1)!=0 || start<16 || start+length+2>returned || data[start+length]!=0 || data[start+length+1]!=0) throw new IOException("Invalid junction reparse data.");
            string text=new UnicodeEncoding(false,false,true).GetString(data,start,length);
            if(text.IndexOf('\0')>=0) throw new IOException("Embedded junction terminator."); return text;
        }
        static void Put16(byte[] data,int offset,int value) {byte[] bytes=BitConverter.GetBytes(checked((ushort)value)); Buffer.BlockCopy(bytes,0,data,offset,2);}
        static void Put32(byte[] data,int offset,uint value) {Buffer.BlockCopy(BitConverter.GetBytes(value),0,data,offset,4);}
    }
}
