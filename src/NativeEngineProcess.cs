using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
namespace ClearDisk {
 internal static class NativeEngineProcess {
  [StructLayout(LayoutKind.Sequential)]struct SecurityAttributes{internal int Length;internal IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]internal bool Inherit;}
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct StartupInfo{internal int Size;internal string Reserved,Desktop,Title;internal uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags;internal ushort Show,ReservedSize;internal IntPtr ReservedData,Input,Output,Error;}
  [StructLayout(LayoutKind.Sequential)]struct StartupInfoEx{internal StartupInfo Info;internal IntPtr Attributes;}
  [StructLayout(LayoutKind.Sequential)]struct ProcessInfo{internal IntPtr Process,Thread;internal uint ProcessId,ThreadId;}
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool CreatePipe(out IntPtr read,out IntPtr write,ref SecurityAttributes security,uint size);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr bytes);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
  [DllImport("kernel32.dll")]static extern void DeleteProcThreadAttributeList(IntPtr list);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcess(string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfoEx startup,out ProcessInfo process);
  [DllImport("kernel32.dll",SetLastError=true)]static extern uint ResumeThread(IntPtr thread);
  [DllImport("kernel32.dll",SetLastError=true)]static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetExitCodeProcess(IntPtr process,out uint code);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateProcess(IntPtr process,uint code);
  [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
  const int DiagnosticLimit=8192;
  sealed class Diagnostics {
   readonly StringBuilder value=new StringBuilder();
   internal void Append(char[] chars,int count){lock(value){int remaining=DiagnosticLimit-value.Length;if(remaining>0)value.Append(chars,0,Math.Min(remaining,count));}}
   public override string ToString(){lock(value)return value.ToString();}
  }
  static string Quote(string value){
   if(value==null||value.IndexOf('\0')>=0)throw new ArgumentException("Invalid process argument.");
   var text=new StringBuilder("\"");int slashes=0;
   foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"'){text.Append('\\',slashes*2+1).Append(c);slashes=0;continue;}text.Append('\\',slashes).Append(c);slashes=0;}
   return text.Append('\\',slashes*2).Append('"').ToString();
  }
  static IntPtr EnvironmentBlock(Dictionary<string,string> overrides){
   var values=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   foreach(DictionaryEntry entry in Environment.GetEnvironmentVariables())values[(string)entry.Key]=(string)entry.Value;
   if(overrides!=null)foreach(var entry in overrides){if(String.IsNullOrEmpty(entry.Key)||entry.Key.IndexOfAny(new[]{'=','\0'})>=0||entry.Value!=null&&entry.Value.IndexOf('\0')>=0)throw new ArgumentException("Invalid child environment.");if(entry.Value==null)values.Remove(entry.Key);else values[entry.Key]=entry.Value;}
   return Marshal.StringToHGlobalUni(String.Join("\0",values.Select(entry=>entry.Key+"="+entry.Value))+"\0\0");
  }
  static Task Drain(IntPtr read,Diagnostics diagnostic){
   var handle=new SafeFileHandle(read,true);
   try{return Task.Factory.StartNew(delegate{
    using(handle)using(var stream=new FileStream(handle,FileAccess.Read,4096,false))using(var reader=new StreamReader(stream,new UTF8Encoding(false,false),true,4096)){
     var chars=new char[2048];int count;while((count=reader.Read(chars,0,chars.Length))>0)diagnostic.Append(chars,count);
    }
   },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);}catch{handle.Dispose();throw;}
  }
  static void Close(ref IntPtr handle){if(handle!=IntPtr.Zero&&handle!=new IntPtr(-1)){CloseHandle(handle);handle=IntPtr.Zero;}}
  internal static string Run(string engine,List<string> arguments,string workingDirectory,Dictionary<string,string> environmentOverrides,CancellationToken token,Action started){
   token.ThrowIfCancellationRequested();
   string command=Quote(engine)+" "+String.Join(" ",arguments.Select(Quote));if(command.Length>=32767)throw new IOException("Engine command line is too long.");
   IntPtr stdoutRead=IntPtr.Zero,stdoutWrite=IntPtr.Zero,stderrRead=IntPtr.Zero,stderrWrite=IntPtr.Zero,stdinRead=IntPtr.Zero,stdinWrite=IntPtr.Zero,attributes=IntPtr.Zero,handleList=IntPtr.Zero,environment=IntPtr.Zero;
   var process=new ProcessInfo();bool attributesInitialized=false;Task output=null,error=null;var diagnostic=new Diagnostics();
   using(var job=new NativeProcessJob()){
    try{
     var security=new SecurityAttributes{Length=Marshal.SizeOf(typeof(SecurityAttributes)),Inherit=true};
     if(!CreatePipe(out stdoutRead,out stdoutWrite,ref security,0)||!CreatePipe(out stderrRead,out stderrWrite,ref security,0)||!CreatePipe(out stdinRead,out stdinWrite,ref security,0))throw new Win32Exception(Marshal.GetLastWin32Error());
     if(!SetHandleInformation(stdoutRead,1,0)||!SetHandleInformation(stderrRead,1,0)||!SetHandleInformation(stdinWrite,1,0))throw new Win32Exception(Marshal.GetLastWin32Error());
     Close(ref stdinWrite); // The child receives an EOF-only stdin.
     IntPtr bytes=IntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref bytes);if(bytes==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());attributes=Marshal.AllocHGlobal(bytes);
     if(!InitializeProcThreadAttributeList(attributes,1,0,ref bytes))throw new Win32Exception(Marshal.GetLastWin32Error());attributesInitialized=true;
     handleList=Marshal.AllocHGlobal(3*IntPtr.Size);Marshal.WriteIntPtr(handleList,0,stdinRead);Marshal.WriteIntPtr(handleList,IntPtr.Size,stdoutWrite);Marshal.WriteIntPtr(handleList,2*IntPtr.Size,stderrWrite);
     if(!UpdateProcThreadAttribute(attributes,0,new IntPtr(0x20002),handleList,new IntPtr(3*IntPtr.Size),IntPtr.Zero,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
     environment=EnvironmentBlock(environmentOverrides);
     var startup=new StartupInfoEx{Info=new StartupInfo{Size=Marshal.SizeOf(typeof(StartupInfoEx)),Flags=0x101,Input=stdinRead,Output=stdoutWrite,Error=stderrWrite},Attributes=attributes};
     token.ThrowIfCancellationRequested();
     // No engine instruction can execute before job assignment succeeds.
     if(!CreateProcess(engine,new StringBuilder(command),IntPtr.Zero,IntPtr.Zero,true,0x08080404,environment,workingDirectory,ref startup,out process))throw new Win32Exception(Marshal.GetLastWin32Error());
     job.Assign(process.Process);
     Close(ref stdoutWrite);Close(ref stderrWrite);Close(ref stdinRead);
     output=Drain(stdoutRead,diagnostic);stdoutRead=IntPtr.Zero;error=Drain(stderrRead,diagnostic);stderrRead=IntPtr.Zero;
     token.ThrowIfCancellationRequested();if(ResumeThread(process.Thread)==UInt32.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());Close(ref process.Thread);
     if(started!=null)started();
     while(true){token.ThrowIfCancellationRequested();uint wait=WaitForSingleObject(process.Process,100);if(wait==0)break;if(wait!=258)throw new Win32Exception(Marshal.GetLastWin32Error());}
     uint exit;if(!GetExitCodeProcess(process.Process,out exit))throw new Win32Exception(Marshal.GetLastWin32Error());
     job.Stop(); // Kill residual descendants before waiting for inherited pipe EOF.
     Task.WaitAll(new[]{output,error});token.ThrowIfCancellationRequested();string result=diagnostic.ToString();
     if(exit!=0)throw new IOException("Czkawka 扫描失败，退出码 "+exit+"。"+result);return result;
    }finally{
     job.Stop();
     if(process.Process!=IntPtr.Zero){TerminateProcess(process.Process,2);WaitForSingleObject(process.Process,5000);}
     Close(ref stdoutWrite);Close(ref stderrWrite);Close(ref stdinRead);Close(ref stdinWrite);Close(ref stdoutRead);Close(ref stderrRead);
     // Job termination closes descendant writers; drains consume only fixed buffers.
     try{if(output!=null)output.Wait(5000);if(error!=null)error.Wait(5000);}catch(AggregateException){}
     Close(ref process.Thread);Close(ref process.Process);
     if(environment!=IntPtr.Zero)Marshal.FreeHGlobal(environment);if(handleList!=IntPtr.Zero)Marshal.FreeHGlobal(handleList);if(attributesInitialized)DeleteProcThreadAttributeList(attributes);if(attributes!=IntPtr.Zero)Marshal.FreeHGlobal(attributes);
    }
   }
  }
 }
}
