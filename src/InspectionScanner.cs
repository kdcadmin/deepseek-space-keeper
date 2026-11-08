using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
namespace ClearDisk {
 public sealed class InspectionScanner {
  readonly SafetyPolicy policy; readonly int workLimit;
  sealed class Root {
   internal string Path,Category;
   internal Root(string path,string category) { Path=path; Category=category; }
  }
  public InspectionScanner(SafetyPolicy safety) : this(safety,500000) { }
  internal InspectionScanner(SafetyPolicy safety,int maximumWork) { if(safety==null) throw new ArgumentNullException("safety"); policy=safety; workLimit=maximumWork; }
  public ScanReport ScanChat(CancellationToken token,Action<ScanProgress> progress) {
   return Execute("chat",true,false,ChatRoots,token,progress);
  }
  IEnumerable<Root> ChatRoots(InspectionBudget budget,ScanReport report,CancellationToken token) {
   string docs=Path.Combine(policy.ProfileRoot,"Documents");
   foreach(string product in new[]{"WeChat Files","xwechat_files","Tencent Files","QQ Files"}) {
    string parent=Path.Combine(docs,product); if(!MetadataWalk.ExistsDirectory(parent,false,report)) continue;
    foreach(string account in MetadataWalk.Children(policy,parent,false,budget,report,token)) {
     string[] leaves=product=="WeChat Files" ? new[]{@"FileStorage\File",@"FileStorage\Video",@"FileStorage\Image"} : product=="xwechat_files" ? new[]{@"msg\file",@"msg\attach",@"msg\video",@"msg\image"} : new[]{"FileRecv","Image","Video"};
     foreach(string leaf in leaves) { budget.Step(token); string path=Path.Combine(account,leaf); if(MetadataWalk.ExistsDirectory(path,false,report)) yield return new Root(path,product+" 媒体 / "+leaf); }
    }
   }
  }
  public ScanReport ScanDownloads(CancellationToken token,Action<ScanProgress> progress) {
   return Execute("downloads",true,false,delegate(InspectionBudget b,ScanReport r,CancellationToken t) { return new[]{new Root(Path.Combine(policy.ProfileRoot,"Downloads"),"下载目录 / 个人文件")}; },token,progress);
  }
  public ScanReport ScanUnknownCaches(CancellationToken token,Action<ScanProgress> progress) {
   return Execute("unknown-caches",false,false,UnknownRoots,token,progress);
  }
  static bool CacheLeaf(string path) { return Array.Exists(new[]{"Cache","Code Cache","GPUCache","cache2"},x=>x.Equals(Path.GetFileName(path),StringComparison.OrdinalIgnoreCase)); }
  IEnumerable<Root> UnknownRoots(InspectionBudget budget,ScanReport report,CancellationToken token) {
   foreach(string storage in new[]{"Local","Roaming"}) {
    string parent=Path.Combine(policy.ProfileRoot,"AppData",storage); if(!MetadataWalk.ExistsDirectory(parent,false,report)) continue;
    foreach(string app in MetadataWalk.Children(policy,parent,false,budget,report,token)) {
     // Application root or vendor/application root: inspect exact cache leaves only.
     foreach(string child in MetadataWalk.Children(policy,app,false,budget,report,token)) {
      if(CacheLeaf(child)) { if(!CuratedRoots.IsTrusted(policy.ProfileRoot,child)) yield return new Root(child,"未知应用缓存 / 仅查看"); continue; }
      foreach(string cache in MetadataWalk.Children(policy,child,false,budget,report,token)) if(CacheLeaf(cache) && !CuratedRoots.IsTrusted(policy.ProfileRoot,cache)) yield return new Root(cache,"未知应用缓存 / 仅查看");
     }
    }
   }
  }
  public ScanReport ScanSystem(CancellationToken token,Action<ScanProgress> progress) {
   string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
   if(String.IsNullOrEmpty(windows)) windows=@"C:\Windows";
   var roots=new List<Root>();
   foreach(string leaf in new[]{"Temp","Logs",@"SoftwareDistribution\Download","WinSxS","Installer","Prefetch","Minidump",@"LiveKernelReports",@"System32\LogFiles",@"System32\config\systemprofile\AppData\Local\CrashDumps"}) roots.Add(new Root(Path.Combine(windows,leaf),"Windows 系统 / "+leaf));
   foreach(string leaf in new[]{@"Microsoft\Windows\WER",@"Microsoft\Windows\DeliveryOptimization",@"Microsoft\Search",@"Microsoft\Windows Defender"}) roots.Add(new Root(Path.Combine(@"C:\ProgramData",leaf),"ProgramData 程序数据 / "+leaf));
   roots.Add(new Root(@"C:\Windows.old","旧版 Windows 安装"));
   foreach(string file in new[]{"hiberfil.sys","pagefile.sys","swapfile.sys"}) roots.Add(new Root(Path.Combine(@"C:\",file),"Windows 管理的虚拟内存文件"));
   return Execute("system",false,true,delegate(InspectionBudget b,ScanReport r,CancellationToken t) { return roots; },token,progress);
  }
  internal ScanReport ScanSystemFixture(IEnumerable<string> paths,CancellationToken token,Action<ScanProgress> progress) {
   return Execute("system",false,true,delegate(InspectionBudget b,ScanReport r,CancellationToken t) { return paths.Select(p=>new Root(p,"System fixture")); },token,progress);
  }
  ScanReport Execute(string mode,bool capture,bool system,Func<InspectionBudget,ScanReport,CancellationToken,IEnumerable<Root>> discover,CancellationToken token,Action<ScanProgress> progress) {
   var report=new ScanReport{Mode=mode,Root=system ? @"C:\" : policy.ProfileRoot}; var clock=Stopwatch.StartNew(); var pulse=new ScanPulse(progress); var budget=new InspectionBudget(workLimit);
   var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   bool detailsLimited=false;
   try {
    foreach(Root spec in discover(budget,report,token)) {
     budget.Step(token); string root;
     try { root=NativeFiles.Normalize(spec.Path); } catch(Exception e) { if(!ScanWalker.IsFileError(e)) throw; report.Skipped++; continue; }
     if(!roots.Add(root)) continue;
     var usage=new FolderUsage{Path=root};
     Action<string,FileStamp> add=delegate(string path,FileStamp stamp) {
      if(!seen.Add(MetadataWalk.Identity(stamp))) return;
      usage.Files++; usage.Bytes+=stamp.Length; report.ScannedFiles++; report.LogicalBytes+=stamp.Length;
      int detailLimit=capture ? 200000 : 2000;
      if(report.Files.Count<detailLimit) {
       var file=new FileRecord{Path=path,Root=root,RuleId=mode,Category=spec.Category,Risk=system ? RiskLevel.Protected : RiskLevel.Review,Length=stamp.Length,LastWriteTicks=stamp.LastWriteTicks,Reason=system ? "仅查看系统文件元数据。由 Windows 管理，工具不提供清理操作。" : mode=="chat" ? "仅查看媒体附件；请保留重要原件。不检查消息数据库。" : mode=="downloads" ? "个人下载文件；请检查内容后手动选择可恢复整理。" : "未知应用缓存仅供查看，可能包含账户或业务数据。"};
       if(capture) try { file.Stamp=policy.Capture(path,root); file.Length=file.Stamp.Length; file.LastWriteTicks=file.Stamp.LastWriteTicks; file.CanOrganize=policy.CanOrganize(file); }
       catch(Exception e) { if(!ScanWalker.IsFileError(e)) throw; file.Reason+=" 无法核验身份或文件受保护，仅供查看。"; }
       report.Files.Add(file);
      } else detailsLimited=true;
      pulse.Send(path,report);
     };
     try {
      FileAttributes attrs;
      try { attrs=File.GetAttributes(root); }
      catch(FileNotFoundException) { if(system) ScanWalker.Warn(report,"检查时路径不存在："+root); continue; }
      catch(DirectoryNotFoundException) { if(system) ScanWalker.Warn(report,"检查时路径不存在："+root); continue; }
      if((attrs&FileAttributes.Directory)!=0) MetadataWalk.Walk(policy,root,true,system,budget,report,token,add);
      else if(system) {
       using(var pins=new NativeFiles.ParentLocks(root)) using(var handle=NativeFiles.Open(root,false,false)) {
        if(!MetadataWalk.AllowedAncestors(policy,Path.GetDirectoryName(root),true)) { report.Skipped++; ScanWalker.Warn(report,"链接或离线祖先目录，占用未知："+root); continue; }
        var stamp=NativeFiles.Stamp(handle); var info=NativeFiles.Info(handle);
        if((info.Attributes&(0x400u|0x1000u|0x40000u|0x10u))!=0 || !String.Equals(stamp.FinalPath,root,StringComparison.OrdinalIgnoreCase)) { report.Skipped++; continue; }
        add(root,stamp);
       }
      }
     } catch(Exception e) { if(!ScanWalker.IsFileError(e)) throw; report.Skipped++; ScanWalker.Warn(report,"无权限或不可用，占用未知："+root); }
     finally { report.Folders.Add(usage); }
    }
   } catch(OperationCanceledException) { report.Cancelled=true; }
   catch(InspectionLimitException) { ScanWalker.Warn(report,"检查达到工作量上限；统计与明细不完整。",true); }
   if(detailsLimited) ScanWalker.Warn(report,"文件明细达到上限（"+(capture ? "200000" : "2000")+"）；统计包含已完成的元数据检查。",true);
   ScanWalker.Warn(report,"重叠目录和硬链接全局只计一次；文件夹统计为独立贡献量，排除链接、云占位文件和无权限项目。",true);
   if(mode=="unknown-caches") ScanWalker.Warn(report,"仅检查 Local/Roaming AppData 中两层应用或厂商目录下名称准确匹配的缓存；未检查更深层或其他名称的缓存。",true);
   if(system) ScanWalker.Warn(report,"系统检查仅读取元数据；不写入、不提权、不修改权限、注册表、服务或 Windows 设置。无权限项目占用仍未知。",true);
   MetadataWalk.Finish(report,clock); return report;
  }
 }
}
