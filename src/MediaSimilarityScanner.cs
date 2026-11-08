using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClearDisk {
 public enum MediaKind {Images,Videos,MusicTags,MusicContent}
 public sealed class MediaSearchOptions {public MediaKind Kind;public long MinimumBytes=16384;public int Threads=4;}
 public sealed class MediaSimilarityScanner {
  public const string EngineHash="2ADBC468E6CDDAD8A089096C5DCF44DD015BF85BBAD5F4ADE34D9EE348D6ADCB";
  readonly SafetyPolicy policy;readonly string engine;
  public MediaSimilarityScanner(SafetyPolicy safety):this(safety,FindEngine()){ }
  public MediaSimilarityScanner(SafetyPolicy safety,string enginePath){if(safety==null)throw new ArgumentNullException("safety");policy=safety;engine=Path.GetFullPath(enginePath);}
  static string FindEngine(){string root=AppDomain.CurrentDomain.BaseDirectory;string nested=Path.Combine(root,"vendor","czkawka-10.0.0","czkawka_cli.exe");if(File.Exists(nested))return nested;return Path.Combine(root,"..","vendor","czkawka-10.0.0","czkawka_cli.exe");}
  internal static string QuoteArgument(string value){
   var s=new StringBuilder("\"");int slashes=0;
   foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"'){s.Append('\\',slashes*2+1);s.Append(c);slashes=0;continue;}s.Append('\\',slashes);slashes=0;s.Append(c);}s.Append('\\',slashes*2);return s.Append('"').ToString();
  }
  static bool MatchesType(string path,MediaKind kind){
   string ext=Path.GetExtension(path).ToLowerInvariant();
   return (kind==MediaKind.Images?new[]{".jpg",".jpeg",".png",".gif",".bmp",".tif",".tiff",".webp",".avif",".heif",".heic"}:kind==MediaKind.Videos?new[]{".mp4",".flv",".mkv",".webm",".vob",".ogv",".gifv",".avi",".mov",".wmv",".mpg",".m4v",".mpeg",".3gp"}:new[]{".mp3",".flac",".ogg",".tta",".wma",".wav",".m4a",".aac",".opus"}).Contains(ext);
  }
  static bool Same(FileStamp a,FileStamp b){return a!=null&&b!=null&&a.Volume==b.Volume&&a.FileId==b.FileId&&a.Length==b.Length&&a.LastWriteTicks==b.LastWriteTicks&&a.Links==1&&b.Links==1&&String.Equals(a.FinalPath,b.FinalPath,StringComparison.OrdinalIgnoreCase);}
  static string TypeName(MediaKind kind){return kind==MediaKind.Images?"相似图片":kind==MediaKind.Videos?"相似视频":kind==MediaKind.MusicTags?"相似音乐（标签）":"相似音乐（音频内容）";}
  public ScanReport Scan(string root,MediaSearchOptions options,CancellationToken token,Action<ScanProgress> progress){
   if(options==null||!Enum.IsDefined(typeof(MediaKind),options.Kind)||options.MinimumBytes<1||options.Threads<1||options.Threads>16)throw new ArgumentException("相似搜索类型、最小字节数或线程数无效。");
   var report=new ScanReport{Mode="similar",Root=root};var watch=Stopwatch.StartNew();var candidates=new List<FileRecord>();var pulse=new ScanPulse(progress);
   try {
    token.ThrowIfCancellationRequested();
    if(!SafetyPolicy.IsWithin(root,policy.ProfileRoot)||!policy.ValidateRoot(root)){ScanWalker.Warn(report,"请选择当前用户目录中的普通文件夹；系统、项目、环境、链接及模型目录受到保护。");return report;}
    using(var engineParents=new NativeFiles.ParentLocks(engine))using(var binary=NativeFiles.OpenData(engine,false)){
     if((NativeFiles.Info(binary).Attributes&0x410)!=0||!String.Equals(NativeFiles.Final(binary),NativeFiles.Normalize(engine),StringComparison.OrdinalIgnoreCase))throw new IOException("搜索引擎路径包含链接或已变化。");
     string hash=BitConverter.ToString(Convert.FromBase64String(NativeFiles.Hash(binary,token))).Replace("-","");if(hash!=EngineHash)throw new IOException("搜索引擎与已核验的官方 Czkawka 10.0.0 字节不一致，已拒绝启动。");
     ScanWalker.Walk(policy,root,false,report,token,delegate(string path,FileInfo info){
      report.ScannedFiles++;report.LogicalBytes+=info.Length;pulse.Send("核验媒体候选："+path,report);
      if(!MatchesType(path,options.Kind)||info.Length<options.MinimumBytes)return;
      if(candidates.Count>=200000){report.Incomplete=true;ScanWalker.Warn(report,"媒体候选达到200000个上限，结果可能不完整。",true);return;}
      try{FileStamp stamp=policy.Capture(path,root);if(stamp.Links!=1){report.Skipped++;return;}candidates.Add(new FileRecord{Path=path,Root=root,Length=stamp.Length,LastWriteTicks=stamp.LastWriteTicks,Stamp=stamp,Risk=RiskLevel.Review});}
      catch(Exception e){if(!ScanWalker.IsFileError(e))throw;report.Skipped++;}
     });
     if(candidates.Count<2)return report;
     using(var workspace=new EngineWorkspace(AppDomain.CurrentDomain.BaseDirectory)){
     string job=workspace.Directory,output=workspace.Output;
     string mode=options.Kind==MediaKind.Images?"image":options.Kind==MediaKind.Videos?"video":"music";
     var args=new List<string>{mode,"-d",Path.GetFullPath(root),"-m",options.MinimumBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),"-p",output,"-D","NONE","-H","-T",options.Threads.ToString(),"-W","-N"};
     if(options.Kind==MediaKind.MusicContent){args.Add("-s");args.Add("CONTENT");}else if(options.Kind==MediaKind.MusicTags){args.Add("-s");args.Add("TAGS");}
     string diagnostic;
     using(var tools=options.Kind==MediaKind.Videos?FfmpegTools.Acquire():null){
      diagnostic=RunEngine(args,job,tools,token,delegate {if(progress!=null)progress(new ScanProgress{Files=report.ScannedFiles,Bytes=report.LogicalBytes,Message="Czkawka 10.0.0 已启动，正在比较"+TypeName(options.Kind)+"；可随时取消。"});});
     }
     token.ThrowIfCancellationRequested();
     if(new FileInfo(output).Length==0)throw new IOException("搜索引擎未产生结果。"+diagnostic);
     report.Files=ParseGroups(workspace.ReadResult(token),candidates,options.Kind,token,report);
     if(!String.IsNullOrWhiteSpace(diagnostic))ScanWalker.Warn(report,"引擎诊断（摘要）："+diagnostic);
     }
    }
   }catch(OperationCanceledException){report.Cancelled=true;report.Incomplete=true;}
   finally{report.Seconds=watch.Elapsed.TotalSeconds;if(report.Cancelled)ScanWalker.Warn(report,"相似搜索已取消，当前结果仅包含已完成部分。",true);ScanWalker.Warn(report,"相似不是内容完全相同，可能误判。默认不选，请逐项核对；音乐标签相同不代表音频相同。",true);}
   return report;
  }
  string RunEngine(List<string> arguments,string job,FfmpegTools tools,CancellationToken token,Action started){
   var environment=new Dictionary<string,string>{{"DISABLE_FILE_LOGGING","1"},{"APPDATA",job},{"LOCALAPPDATA",job},{"CZKAWKA_CONFIG_PATH",job},{"CZKAWKA_CACHE_PATH",job}};
   if(tools!=null)environment["PATH"]=tools.Directory+";"+Environment.SystemDirectory+";"+Environment.GetFolderPath(Environment.SpecialFolder.Windows);
   return NativeEngineProcess.Run(engine,arguments,Path.GetDirectoryName(engine),environment,token,started);
  }
  internal List<FileRecord> ParseForTest(string json,IEnumerable<FileRecord> candidates,MediaKind kind){return ParseGroups(json,candidates,kind,CancellationToken.None,new ScanReport());}
  internal List<FileRecord> ParseBoundedForTest(string json,IEnumerable<FileRecord> candidates,ScanReport report,int limit,CancellationToken token,Action<FileRecord> validated){return ParseGroups(json,candidates,MediaKind.Images,token,report,limit,validated);}
  List<FileRecord> ParseGroups(string json,IEnumerable<FileRecord> candidateFiles,MediaKind kind,CancellationToken token,ScanReport report,int maximumResults=50000,Action<FileRecord> validated=null){
   object parsed;try{parsed=new JavaScriptSerializer{MaxJsonLength=64*1024*1024,RecursionLimit=32}.DeserializeObject(json);}catch(ArgumentException e){throw new IOException("引擎JSON结果格式无效。",e);}
   var groups=parsed as object[];if(groups==null)throw new IOException("引擎JSON分组结构无效。");
   var candidates=candidateFiles.ToDictionary(f=>NativeFiles.Normalize(f.Path),StringComparer.OrdinalIgnoreCase);var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var result=report.Files;int groupNumber=0;
   foreach(object rawGroup in groups){token.ThrowIfCancellationRequested();if(maximumResults-result.Count<2){report.Incomplete=true;ScanWalker.Warn(report,"相似结果明细达到显示上限，结果不完整。",true);break;}var rows=rawGroup as object[];if(rows==null)throw new IOException("引擎JSON组内结构无效。");var valid=new List<FileRecord>();
    foreach(object item in rows){token.ThrowIfCancellationRequested();if(result.Count+valid.Count>=maximumResults){report.Incomplete=true;ScanWalker.Warn(report,"相似结果明细达到显示上限，结果不完整。",true);break;}var row=item as Dictionary<string,object>;object pathValue;if(row==null||!row.TryGetValue("path",out pathValue)||!(pathValue is string)){report.Skipped++;continue;}FileRecord file;string path;
     try{path=NativeFiles.Normalize((string)pathValue);}catch{report.Skipped++;continue;}
     if(!candidates.TryGetValue(path,out file)||used.Contains(path)){report.Skipped++;continue;}
     try{
      using(var parents=new NativeFiles.ParentLocks(file.Path))using(var handle=NativeFiles.OpenData(file.Path,false)){
       if(!Same(file.Stamp,NativeFiles.Stamp(handle))){report.Skipped++;continue;}
       file.ContentHash=NativeFiles.Hash(handle,token);FileStamp current=policy.Capture(file.Path,file.Root);if(!Same(file.Stamp,current)){report.Skipped++;continue;}file.Stamp=current;
      }
      if(validated!=null)validated(file);token.ThrowIfCancellationRequested();valid.Add(file);used.Add(path);
     }catch(Exception e){if(!ScanWalker.IsFileError(e))throw;report.Skipped++;}
    }
    if(valid.Count<2)continue;valid=valid.OrderByDescending(f=>f.Length).ThenBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToList();string group="similar-"+(++groupNumber);
    for(int i=0;i<valid.Count;i++){token.ThrowIfCancellationRequested();FileRecord file=valid[i];file.IsKeeper=i==0;file.KeeperRecord=i==0?null:valid[0];file.DuplicateGroup=group;file.RuleId="czkawka10-"+kind;file.Category=TypeName(kind);file.Reason=i==0?"本组保留项；相似结果须人工核对。":"同版本 Czkawka 搜索结果；相似并不代表完全相同，请预览并人工核对后再选择隔离。";file.CanOrganize=policy.CanOrganize(file);file.CanDelete=false;}
    token.ThrowIfCancellationRequested();result.AddRange(valid);
   }
   return result;
  }
 }
}
