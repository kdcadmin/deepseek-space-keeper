using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace ClearDisk {
 // Resolve PATH components individually: unmatched quotes in an earlier entry can
 // otherwise make Rust's dependency lookup return no matches without a CLI error.
 // Only the child receives the bounded PATH; the user's environment is untouched.
 internal sealed class FfmpegTools:IDisposable {
  readonly List<IDisposable> locks=new List<IDisposable>();
  internal string Directory {get;private set;}
  FfmpegTools(string directory){
   Directory=NativeFiles.Normalize(directory);
   try{
    foreach(string name in new[]{"ffmpeg.exe","ffprobe.exe"}){
     string path=Path.Combine(Directory,name);locks.Add(new NativeFiles.ParentLocks(path));
     SafeFileHandle handle=NativeFiles.OpenData(path,false);locks.Add(handle);
     if((NativeFiles.Info(handle).Attributes&0x410)!=0||!String.Equals(NativeFiles.Final(handle),NativeFiles.Normalize(path),StringComparison.OrdinalIgnoreCase))throw new IOException("FFmpeg 工具路径包含链接或已变化。");
    }
   }catch{Dispose();throw;}
  }
  internal static FfmpegTools Acquire(){return Acquire(Environment.GetEnvironmentVariable("PATH"));}
  internal static FfmpegTools Acquire(string searchPath){
   foreach(string raw in (searchPath??"").Split(';')){
    string directory=raw.Trim().Trim('"');if(directory.Length==0||!Path.IsPathRooted(directory))continue;
    try{
     if(File.Exists(Path.Combine(directory,"ffmpeg.exe"))&&File.Exists(Path.Combine(directory,"ffprobe.exe")))return new FfmpegTools(directory);
    }catch(Exception e){if(!(e is IOException)&&!(e is UnauthorizedAccessException)&&!(e is System.ComponentModel.Win32Exception)&&!(e is ArgumentException))throw;}
   }
   throw new IOException("相似视频需要已安装的 ffmpeg.exe 和 ffprobe.exe。未找到可核验的同目录工具，扫描未执行；这不表示没有相似视频。");
  }
  public void Dispose(){for(int i=locks.Count-1;i>=0;i--)locks[i].Dispose();locks.Clear();}
 }
}
