using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace ClearDisk {
 public enum ChatProduct { WeChat4, QQGroup2 }
 public sealed class ChatScanOptions {
  public ChatProduct Product;
  public string Root,SelectedAccount;
  public bool Images=true,Files=true,Videos=true;
  public int RetentionDays=365;
 }
 public sealed class ChatInventoryScanner {
  readonly SafetyPolicy policy; readonly int workLimit,recordLimit; readonly DateTime? fixtureNow;
  enum Kind { Attach,Image,File,Video }
  sealed class Pending { internal string Path,MediaRoot; internal Kind Kind; }
  static readonly HashSet<string> Images=new HashSet<string>(new[]{".jpg",".jpeg",".png",".gif",".bmp",".webp",".heic",".heif",".avif",".tif",".tiff",".ico",".svg",".raw",".cr2",".nef",".dng",".arw",".raf"},StringComparer.OrdinalIgnoreCase);
  static readonly HashSet<string> Videos=new HashSet<string>(new[]{".mp4",".mkv",".mov",".avi",".wmv",".webm",".m4v",".mpg",".mpeg"},StringComparer.OrdinalIgnoreCase);
  static readonly HashSet<string> Databases=new HashSet<string>(new[]{".db",".db3",".sqlite",".sqlite3",".db-wal",".db-shm",".sqlite-wal",".sqlite-shm",".sqlite3-wal",".sqlite3-shm"},StringComparer.OrdinalIgnoreCase);
  public ChatInventoryScanner(SafetyPolicy safety):this(safety,500000,200000,null){}
  internal ChatInventoryScanner(SafetyPolicy safety,int maximumWork,int maximumRecords,DateTime? now){if(safety==null)throw new ArgumentNullException("safety");policy=safety;workLimit=Math.Max(0,maximumWork);recordLimit=Math.Max(0,maximumRecords);fixtureNow=now;}
  public static string DefaultRoot(SafetyPolicy safety,ChatProduct product){if(safety==null)throw new ArgumentNullException("safety");return Path.Combine(safety.ProfileRoot,"Documents",product==ChatProduct.WeChat4?"xwechat_files":"Tencent Files");}
  static bool Numeric(string value){return !String.IsNullOrEmpty(value)&&value.All(c=>c>='0'&&c<='9');}
  static bool Database(string path){return Databases.Contains(Path.GetExtension(path))||new[]{"-wal","-shm","-journal",".wal",".shm",".journal"}.Any(suffix=>path.EndsWith(suffix,StringComparison.OrdinalIgnoreCase));}
  bool Account(ChatProduct product,string path,ScanReport report){
   string name=Path.GetFileName(path);
   if(product==ChatProduct.QQGroup2)return Numeric(name);
   if(new[]{"All Users","all_users","shared","cache","config"}.Any(x=>x.Equals(name,StringComparison.OrdinalIgnoreCase)))return false;
   return name.StartsWith("wxid_",StringComparison.OrdinalIgnoreCase)||MetadataWalk.ExistsDirectory(Path.Combine(path,"msg"),false,report);
  }
  bool Allowed(string path,ScanReport report){
   try {
    string full=NativeFiles.Normalize(path);
    if(!SafetyPolicy.IsWithin(full,policy.ProfileRoot)||!policy.ValidateInspectionRoot(full)||policy.IsProtected(full)||!MetadataWalk.AllowedAncestors(policy,full,false)){
     Unknown(report,"当前用户 C 盘范围之外，或目录受保护、链接、离线、项目/运行环境不可用："+path);return false;
    }
    return true;
   }catch(Exception e){if(!ScanWalker.IsFileError(e))throw;Unknown(report,"无权限或路径不可用："+path);return false;}
  }
  static void Unknown(ScanReport report,string reason){report.Skipped++;report.Incomplete=true;ScanWalker.Warn(report,reason+"；占用未知。");}
  List<string> Accounts(ChatProduct product,string root,InspectionBudget budget,ScanReport report,CancellationToken token){
   var result=new List<string>();budget.Step(token);
   if(!Allowed(root,report))return result;
   if(Account(product,root,report)){result.Add(root);return result;}
   foreach(string path in MetadataWalk.Children(policy,root,false,budget,report,token)){
    budget.Step(token);if(Account(product,path,report)&&Allowed(path,report))result.Add(path);
   }
   return result.OrderBy(x=>Path.GetFileName(x),StringComparer.OrdinalIgnoreCase).ToList();
  }
  public List<string> DiscoverAccounts(ChatProduct product,string root,CancellationToken token){string warning;return DiscoverAccounts(product,root,token,out warning);}
  internal List<string> DiscoverAccounts(ChatProduct product,string root,CancellationToken token,out string warning){
   var report=new ScanReport();var accounts=new List<string>();
   try{accounts=Accounts(product,NativeFiles.Normalize(root),new InspectionBudget(1000),report,token);}
   catch(InspectionLimitException){report.Incomplete=true;ScanWalker.Warn(report,"账号发现达到 1000 项工作量上限，请选择更具体的目录。");}
   catch(OperationCanceledException){report.Incomplete=true;ScanWalker.Warn(report,"账号发现已取消。");}
   catch(Exception e){if(!ScanWalker.IsFileError(e))throw;Unknown(report,"无法读取账号目录");}
   warning=String.Join("\r\n",report.Warnings.ToArray());return accounts.Select(Path.GetFileName).ToList();
  }
  static void Validate(ChatScanOptions options){
   if(options==null)throw new ArgumentNullException("options");
   if(options.Product!=ChatProduct.WeChat4&&options.Product!=ChatProduct.QQGroup2)throw new ArgumentException("未知聊天产品。");
   if(options.RetentionDays<0)throw new ArgumentException("保留天数必须是大于等于 0 的整数。");
   if(options.Product==ChatProduct.QQGroup2?!options.Images:!options.Images&&!options.Files&&!options.Videos)throw new ArgumentException("请至少选择一种可扫描的附件类型。");
   if(!String.IsNullOrEmpty(options.SelectedAccount)&&(options.SelectedAccount=="."||options.SelectedAccount==".."||options.SelectedAccount.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new ArgumentException("请选择有效账号名称。");
  }
  public ScanReport Scan(ChatScanOptions options,CancellationToken token,Action<ScanProgress> progress){
   Validate(options);
   // Copy options once: UI edits cannot change a running scan's scope.
   var request=new ChatScanOptions{Product=options.Product,Root=options.Root,SelectedAccount=options.SelectedAccount,Images=options.Images,Files=options.Files,Videos=options.Videos,RetentionDays=options.RetentionDays};
   var report=new ScanReport{Mode="chat",Root=request.Root};var clock=Stopwatch.StartNew();var budget=new InspectionBudget(workLimit);var pulse=new ScanPulse(progress);
   var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var summaries=new Dictionary<string,FolderUsage>(StringComparer.OrdinalIgnoreCase);
   DateTime now=fixtureNow??DateTime.UtcNow;DateTime cutoff=request.RetentionDays>=(now-DateTime.MinValue).TotalDays?DateTime.MinValue:now.AddDays(-request.RetentionDays);
   try{
    budget.Step(token);string root=NativeFiles.Normalize(String.IsNullOrWhiteSpace(request.Root)?DefaultRoot(policy,request.Product):request.Root);report.Root=root;
    var accounts=Accounts(request.Product,root,budget,report,token);
    if(!String.IsNullOrEmpty(request.SelectedAccount))accounts=accounts.Where(a=>Path.GetFileName(a).Equals(request.SelectedAccount,StringComparison.OrdinalIgnoreCase)).ToList();
    if(accounts.Count==0){if(!String.IsNullOrEmpty(request.SelectedAccount))Unknown(report,"指定账号不存在、已移动或不可访问；该账号范围未完成检查："+request.SelectedAccount);else ScanWalker.Warn(report,"未发现普通账号；请检查数据目录。",true);}
    foreach(string account in accounts){
     budget.Step(token);
     if(request.Product==ChatProduct.QQGroup2)Walk(Path.Combine(account,@"Image\Group2"),Kind.Image,Path.GetFileName(account),request,cutoff,budget,report,token,pulse,seen,summaries);
     else {
      if(request.Images)Walk(Path.Combine(account,@"msg\image"),Kind.Image,Path.GetFileName(account),request,cutoff,budget,report,token,pulse,seen,summaries);
      if(request.Files)Walk(Path.Combine(account,@"msg\file"),Kind.File,Path.GetFileName(account),request,cutoff,budget,report,token,pulse,seen,summaries);
      if(request.Videos)Walk(Path.Combine(account,@"msg\video"),Kind.Video,Path.GetFileName(account),request,cutoff,budget,report,token,pulse,seen,summaries);
      Walk(Path.Combine(account,@"msg\attach"),Kind.Attach,Path.GetFileName(account),request,cutoff,budget,report,token,pulse,seen,summaries);
     }
    }
   }catch(OperationCanceledException){report.Cancelled=true;}
   catch(InspectionLimitException){report.Incomplete=true;ScanWalker.Warn(report,"扫描达到工作量或文件明细上限；统计与明细不完整。",true);}
   catch(Exception e){if(!ScanWalker.IsFileError(e))throw;Unknown(report,"无法检查聊天目录："+e.Message);}
   report.Folders.AddRange(summaries.Values.OrderByDescending(x=>x.Path,StringComparer.OrdinalIgnoreCase));
   ScanWalker.Warn(report,"年月分组与保留天数依据 UTC 文件最后修改时间，并非实际聊天创建时间。汇总标签仅供查看，不是可打开的目录。",true);
   ScanWalker.Warn(report,"不读取聊天数据库或消息内容，不解密媒体；未知或加密格式仅供查看。重叠目录和硬链接按文件身份只计一次。",true);
   MetadataWalk.Finish(report,clock);return report;
  }
  static bool Enabled(ChatScanOptions o,Kind kind){return kind==Kind.Image?o.Images:kind==Kind.File?o.Files:kind==Kind.Video&&o.Videos;}
  static Kind AttachmentKind(string name,Kind previous){if(name.Equals("Img",StringComparison.OrdinalIgnoreCase)||name.Equals("Image",StringComparison.OrdinalIgnoreCase))return Kind.Image;if(name.Equals("Video",StringComparison.OrdinalIgnoreCase))return Kind.Video;if(name.Equals("File",StringComparison.OrdinalIgnoreCase))return Kind.File;return previous;}
  void Walk(string root,Kind kind,string account,ChatScanOptions options,DateTime cutoff,InspectionBudget budget,ScanReport report,CancellationToken token,ScanPulse pulse,HashSet<string> seen,Dictionary<string,FolderUsage> summaries){
   budget.Step(token);if(!MetadataWalk.ExistsDirectory(root,false,report))return;
   var pending=new Stack<Pending>();pending.Push(new Pending{Path=root,MediaRoot=root,Kind=kind});
   while(pending.Count>0){
    budget.Step(token);var current=pending.Pop();
    try{
     using(var pins=new NativeFiles.ParentLocks(Path.Combine(current.Path,".chat-metadata"))){
      if(!Allowed(current.Path,report))continue;
      foreach(string path in Directory.EnumerateFileSystemEntries(current.Path)){
       budget.Step(token);
       try{
        FileAttributes attrs=File.GetAttributes(path);bool directory=(attrs&FileAttributes.Directory)!=0;
        if((attrs&(FileAttributes.ReparsePoint|FileAttributes.Offline|FileAttributes.System|(FileAttributes)0x40000))!=0){Unknown(report,"链接、系统或离线项目已跳过："+path);continue;}
        if(directory){
         Kind next=kind==Kind.Attach?AttachmentKind(Path.GetFileName(path),current.Kind):current.Kind;
         if(next!=Kind.Attach&&!Enabled(options,next))continue;
         pending.Push(new Pending{Path=path,Kind=next,MediaRoot=next!=current.Kind?path:current.MediaRoot});continue;
        }
        string ext=Path.GetExtension(path);
        // Never open databases, including their journal companions, even for identity metadata.
        if(Database(path)||current.Kind==Kind.Attach||!Enabled(options,current.Kind))continue;
        if(options.Product==ChatProduct.QQGroup2&&!Images.Contains(ext)&&!ext.Equals(".dat",StringComparison.OrdinalIgnoreCase)&&!ext.Equals(".wxgf",StringComparison.OrdinalIgnoreCase))continue;
        if(policy.IsProtected(path)){Unknown(report,"受保护或运行环境文件已跳过："+path);continue;}
        using(var handle=NativeFiles.Open(path,false,false)){
         var info=NativeFiles.Info(handle);if((info.Attributes&(0x400u|0x1000u|0x40000u|0x10u|4u))!=0){Unknown(report,"文件已变为链接、系统或离线项目："+path);continue;}
         var stamp=NativeFiles.Stamp(handle);
         if(!String.Equals(stamp.FinalPath,NativeFiles.Normalize(path),StringComparison.OrdinalIgnoreCase)){Unknown(report,"文件最终路径变化："+path);continue;}
         if(stamp.LastWriteTicks>=cutoff.Ticks||!seen.Add(MetadataWalk.Identity(stamp)))continue;
         if(report.Files.Count>=recordLimit){ScanWalker.Warn(report,"文件明细达到 "+recordLimit+" 条上限；其余文件占用未知。",true);throw new InspectionLimitException();}
         bool positive=current.Kind==Kind.Image?Images.Contains(ext):current.Kind==Kind.Video?Videos.Contains(ext):policy.PersonalOrganizationPath(path);
         var file=new FileRecord{Path=path,Root=current.MediaRoot,RuleId=options.Product==ChatProduct.WeChat4?"wechat4":"qq-group2",Category=(options.Product==ChatProduct.WeChat4?"微信 4":"QQ Group2")+" / "+account+" / "+(current.Kind==Kind.Image?"图片":current.Kind==Kind.File?"文件":"视频"),Length=stamp.Length,LastWriteTicks=stamp.LastWriteTicks,Risk=RiskLevel.Review,Reason="附件默认不选；请检查原件后手动选择可恢复整理。"};
         if(positive){
          try{file.Stamp=policy.Capture(path,current.MediaRoot);if(MetadataWalk.Identity(file.Stamp)!=MetadataWalk.Identity(stamp)||file.Stamp.Length!=stamp.Length||file.Stamp.LastWriteTicks!=stamp.LastWriteTicks){file.Stamp=null;Unknown(report,"文件在核验时已变化："+path);}else file.CanOrganize=policy.CanOrganize(file);}
          catch(Exception e){if(!ScanWalker.IsFileError(e))throw;Unknown(report,"无法核验文件身份："+path);}
          if(!file.CanOrganize)file.Reason="文件身份、存储位置或硬链接状态不符合既有整理策略，仅供查看。";
         }else file.Reason="未知或加密媒体格式（包括 .dat），不解密、不签发整理权限，仅供查看。";
         string label="只读汇总 / "+file.Category+" / "+new DateTime(stamp.LastWriteTicks,DateTimeKind.Utc).ToString("yyyy-MM",CultureInfo.InvariantCulture);
         FolderUsage usage;if(!summaries.TryGetValue(label,out usage)){usage=new FolderUsage{Path=label};summaries.Add(label,usage);}usage.Files++;usage.Bytes+=stamp.Length;
         report.Files.Add(file);report.ScannedFiles++;report.LogicalBytes+=stamp.Length;pulse.Send(path,report);
        }
       }catch(Exception e){if(!ScanWalker.IsFileError(e))throw;Unknown(report,"无权限或文件检查失败："+path);}
      }
     }
    }catch(Exception e){if(!ScanWalker.IsFileError(e))throw;Unknown(report,"目录无权限或已变化："+current.Path);}
   }
  }
 }
}
