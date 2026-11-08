using System;using System.Collections.Generic;using System.Diagnostics;using System.IO;using System.Linq;using System.Threading;
namespace ClearDisk {
 public sealed class DirectoryUsageItem {
  public string Path,Name,Status;public long LogicalBytes,Files,Skipped;public bool Measured,Incomplete;
 }
 public sealed class DirectoryUsageReport {
  public string Root;public List<DirectoryUsageItem> Items=new List<DirectoryUsageItem>();public List<string> Warnings=new List<string>();
  public long RootFileBytes,RootFileCount,UniqueLogicalBytes,UniqueFiles,Skipped;public bool Cancelled,Incomplete;public double Seconds;
 }
 public sealed class DirectoryUsageScanner {
  readonly SafetyPolicy policy;readonly int maximum;
  public DirectoryUsageScanner(SafetyPolicy safety):this(safety,2000000){}
  internal DirectoryUsageScanner(SafetyPolicy safety,int maximumWork){if(safety==null)throw new ArgumentNullException("safety");policy=safety;maximum=maximumWork;}
  bool OtherProfile(string path){string users=Path.GetDirectoryName(policy.ProfileRoot);return !String.IsNullOrEmpty(users)&&SafetyPolicy.IsWithin(path,users)&&!String.Equals(path,users,StringComparison.OrdinalIgnoreCase)&&!SafetyPolicy.IsWithin(path,policy.ProfileRoot);}
  public DirectoryUsageReport Scan(string root,CancellationToken token,Action<ScanProgress> progress){
   if(String.IsNullOrWhiteSpace(root)||!root.StartsWith(@"C:\",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("请选择C盘普通绝对目录。");root=NativeFiles.Normalize(root);
   var report=new DirectoryUsageReport{Root=root};var state=new ScanReport();var budget=new InspectionBudget(maximum);var pulse=new ScanPulse(progress);var global=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var direct=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var clock=Stopwatch.StartNew();
   Action<string,FileStamp> total=delegate(string path,FileStamp stamp){if(global.Add(MetadataWalk.Identity(stamp))){report.UniqueFiles++;report.UniqueLogicalBytes+=stamp.Length;state.ScannedFiles++;state.LogicalBytes+=stamp.Length;}pulse.Send(path,state);};
   try{
    budget.Step(token);
    if(OtherProfile(root)){state.Skipped++;ScanWalker.Warn(state,"其他用户的目录不在检查范围内，占用未知。",true);return report;}
    if(!MetadataWalk.ExistsDirectory(root,true,state)){state.Skipped++;ScanWalker.Warn(state,"目录不存在或无权限，占用未知："+root,true);return report;}
    MetadataWalk.Walk(policy,root,false,true,budget,state,token,delegate(string path,FileStamp stamp){if(direct.Add(MetadataWalk.Identity(stamp))){report.RootFileCount++;report.RootFileBytes+=stamp.Length;}total(path,stamp);});
    foreach(string child in MetadataWalk.Children(policy,root,true,budget,state,token)){
     budget.Step(token);if(report.Items.Count>=10000){report.Incomplete=true;ScanWalker.Warn(state,"当前层子目录达到10000项上限，结果不完整。",true);break;}
     var item=new DirectoryUsageItem{Path=child,Name=Path.GetFileName(child)};report.Items.Add(item);
     if(OtherProfile(child)){item.Incomplete=true;item.Status="其他用户目录，未检查。";state.Skipped++;continue;}
     string measuredRoot=child;string users=Path.GetDirectoryName(policy.ProfileRoot);
     if(String.Equals(child,users,StringComparison.OrdinalIgnoreCase)){measuredRoot=policy.ProfileRoot;item.Incomplete=true;ScanWalker.Warn(state,"Users 目录仅计量当前用户；其他用户占用未知。",true);}
     long skipped=state.Skipped;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
     try{item.Measured=true;MetadataWalk.Walk(policy,measuredRoot,true,true,budget,state,token,delegate(string path,FileStamp stamp){if(seen.Add(MetadataWalk.Identity(stamp))){item.Files++;item.LogicalBytes+=stamp.Length;}total(path,stamp);});}
     catch(OperationCanceledException){item.Incomplete=true;throw;}catch(InspectionLimitException){item.Incomplete=true;throw;}
     finally{item.Skipped=state.Skipped-skipped;item.Incomplete=item.Incomplete||item.Skipped>0;if(item.Incomplete&&item.Files==0)item.Measured=false;item.Status=item.Incomplete?"部分结果：存在跳过或未检查项目。":"只读计量完成。";}
    }
   }catch(OperationCanceledException){report.Cancelled=true;report.Incomplete=true;ScanWalker.Warn(state,"目录查询已取消，结果不完整。",true);}
   catch(InspectionLimitException){report.Incomplete=true;ScanWalker.Warn(state,"目录查询达到工作量上限，结果不完整。",true);}
   finally{report.Seconds=clock.Elapsed.TotalSeconds;report.Skipped=state.Skipped;report.Incomplete=report.Incomplete||state.Skipped>0||report.Items.Any(i=>i.Incomplete);if(state.Skipped>0)ScanWalker.Warn(state,"已跳过 "+state.Skipped+" 个无权限、链接、占位或范围外项目，占用未知。",true);report.Warnings.AddRange(state.Warnings);}
   return report;
  }
 }
}
