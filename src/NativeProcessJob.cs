using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClearDisk {
 internal sealed class NativeProcessJob : IDisposable {
  sealed class JobHandle:SafeHandleZeroOrMinusOneIsInvalid{public JobHandle():base(true){}protected override bool ReleaseHandle(){return CloseHandle(handle);}}
  [StructLayout(LayoutKind.Sequential)]struct BasicLimits{public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWorkingSet,MaxWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;}
  [StructLayout(LayoutKind.Sequential)]struct IoCounters{public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;}
  [StructLayout(LayoutKind.Sequential)]struct ExtendedLimits{public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern JobHandle CreateJobObject(IntPtr attributes,string name);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(JobHandle job,int kind,ref ExtendedLimits limits,uint length);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(JobHandle job,IntPtr process);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateJobObject(JobHandle job,uint code);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool CloseHandle(IntPtr handle);
  readonly JobHandle job;
  internal NativeProcessJob(){job=CreateJobObject(IntPtr.Zero,null);if(job.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());var limits=new ExtendedLimits{Basic=new BasicLimits{Flags=0x2000}};if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf(typeof(ExtendedLimits)))){int error=Marshal.GetLastWin32Error();job.Dispose();throw new Win32Exception(error);}}
  internal void Assign(IntPtr process){if(!AssignProcessToJobObject(job,process))throw new Win32Exception(Marshal.GetLastWin32Error());}
  internal void Stop(){if(!job.IsClosed&&!job.IsInvalid)TerminateJobObject(job,2);}
  public void Dispose(){job.Dispose();}
 }
}
