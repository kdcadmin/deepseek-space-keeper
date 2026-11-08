using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ClearDisk {
 // Every output directory remains pinned. CREATE_NEW reserves a regular output
 // file whose open handle prevents path replacement while the native tool writes.
 internal sealed class EngineWorkspace:IDisposable {
  readonly List<IDisposable> pins=new List<IDisposable>();FileStream result;
  internal string Directory {get;private set;}internal string Output {get;private set;}
  internal EngineWorkspace(string applicationDirectory){
   try{
    string current=NativeFiles.Normalize(applicationDirectory);pins.Add(new NativeFiles.ParentLocks(Path.Combine(current,".engine-pin")));
    foreach(string name in new[]{"reports","engine-jobs",Guid.NewGuid().ToString("N")}){
     current=Path.Combine(current,name);System.IO.Directory.CreateDirectory(current);pins.Add(new NativeFiles.ParentLocks(Path.Combine(current,".engine-pin")));
    }
    Directory=current;Output=Path.Combine(current,"result.json");
    result=new FileStream(Output,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.ReadWrite);
    var info=NativeFiles.Info(result.SafeFileHandle);if((info.Attributes&0x410)!=0||info.Links!=1||!String.Equals(NativeFiles.Final(result.SafeFileHandle),Output,StringComparison.OrdinalIgnoreCase))throw new IOException("引擎结果文件路径或链接数异常。");
   }catch{Dispose();throw;}
  }
  internal string ReadResult(CancellationToken token){
   token.ThrowIfCancellationRequested();
   long length=result.Length;if(length>64*1024*1024)throw new IOException("引擎结果超过64MB安全上限，请缩小扫描目录。");
   result.Position=0;byte[] bytes=new byte[checked((int)length)];int count=0;
   while(count<bytes.Length){token.ThrowIfCancellationRequested();int read=result.Read(bytes,count,Math.Min(65536,bytes.Length-count));if(read==0)throw new IOException("读取引擎结果时文件已截断。");count+=read;}
   if(result.Length!=length)throw new IOException("读取引擎结果时文件长度已变化。");
   return Encoding.UTF8.GetString(bytes);
  }
  public void Dispose(){if(result!=null){result.Dispose();result=null;}for(int i=pins.Count-1;i>=0;i--)pins[i].Dispose();pins.Clear();}
 }
}
