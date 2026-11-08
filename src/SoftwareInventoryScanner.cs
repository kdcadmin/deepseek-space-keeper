using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace ClearDisk {
 public sealed class SoftwareInstall {
  public string Id,Name,Version,Publisher,InstallDate,InstallLocation;
  public long? EstimatedBytes;
 }
 public sealed class SoftwareUsage {
  public SoftwareInstall Installation;public string Root,Status;
  public long LogicalBytes,Files,Skipped;public bool Measured,Incomplete;
 }
 public sealed class SoftwareInventoryReport {
  public List<SoftwareUsage> Items=new List<SoftwareUsage>();public List<string> Warnings=new List<string>();
  public long UniqueLogicalBytes,UniqueFiles;public bool Cancelled,Incomplete;public double Seconds;
 }
 public sealed class SoftwareInventoryScanner {
  readonly SafetyPolicy policy;readonly int maximum;
  public SoftwareInventoryScanner(SafetyPolicy safety):this(safety,2000000){}
  internal SoftwareInventoryScanner(SafetyPolicy safety,int workLimit){if(safety==null)throw new ArgumentNullException("safety");policy=safety;maximum=workLimit;}
  static void Warn(SoftwareInventoryReport report,string warning){report.Incomplete=true;if(report.Warnings.Count<100&&!report.Warnings.Contains(warning))report.Warnings.Add(warning);}
  public SoftwareInventoryReport ScanInstalled(CancellationToken token,Action<ScanProgress> progress){
   var source=new SoftwareInventoryReport();List<SoftwareInstall> installed;try{installed=ReadInstalled(token,source);}catch(OperationCanceledException){source.Cancelled=true;Warn(source,"安装软件清单读取已取消，未完成计量。");return source;}var report=Scan(installed,token,progress);foreach(string warning in source.Warnings)Warn(report,warning);return report;
  }
  static string Text(RegistryKey key,string name){object value=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);return value is string?((string)value).Trim():"";}
  static List<SoftwareInstall> ReadInstalled(CancellationToken token,SoftwareInventoryReport report){
   var entries=new List<SoftwareInstall>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var hive in new[]{RegistryHive.LocalMachine,RegistryHive.CurrentUser})foreach(var view in Environment.Is64BitOperatingSystem?new[]{RegistryView.Registry64,RegistryView.Registry32}:new[]{RegistryView.Registry32}){
    try{
     token.ThrowIfCancellationRequested();using(var root=RegistryKey.OpenBaseKey(hive,view))using(var uninstall=root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall",false)){
      if(uninstall==null)continue;
      foreach(string name in uninstall.GetSubKeyNames()){
       token.ThrowIfCancellationRequested();if(entries.Count>=5000){Warn(report,"安装软件条目达到5000项上限，目录清单不完整。");return entries;}
       try{using(var key=uninstall.OpenSubKey(name,false)){
        if(key==null)continue;string display=Text(key,"DisplayName");if(display.Length==0||Object.Equals(key.GetValue("SystemComponent"),1))continue;
        string location=Text(key,"InstallLocation"),version=Text(key,"DisplayVersion");string identity=display+"\0"+version+"\0"+location;if(!seen.Add(identity))continue;
        long? estimate=null;object registered=key.GetValue("EstimatedSize");if(registered!=null)try{long kb=Convert.ToInt64(registered,CultureInfo.InvariantCulture);if(kb>=0&&kb<=Int64.MaxValue/1024)estimate=kb*1024;}catch(FormatException){}catch(InvalidCastException){}catch(OverflowException){}
        entries.Add(new SoftwareInstall{Id=hive+"/"+view+"/"+name,Name=display,Version=version,Publisher=Text(key,"Publisher"),InstallDate=Text(key,"InstallDate"),InstallLocation=location,EstimatedBytes=estimate});
       }}catch(Exception e){if(!ScanWalker.IsFileError(e))throw;Warn(report,"无法读取安装软件登记项（占用未知）："+hive+"/"+view+"/"+name);}
      }
     }
    }catch(Exception e){if(!ScanWalker.IsFileError(e))throw;Warn(report,"无法读取安装软件注册表范围："+hive+"/"+view+"；清单可能缺失。");}
   }
   return entries;
  }
  bool Resolve(string expression,out string root){
   root=null;if(String.IsNullOrWhiteSpace(expression))return false;
   try{
    string expanded=Environment.ExpandEnvironmentVariables(expression.Trim().Trim('"'));
    if(!expanded.StartsWith(@"C:\",StringComparison.OrdinalIgnoreCase))return false;
    root=NativeFiles.Normalize(expanded);
    string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows),users=Path.GetDirectoryName(policy.ProfileRoot);
    if(root.Length<=3||SafetyPolicy.IsWithin(root,windows)||String.Equals(root,policy.ProfileRoot,StringComparison.OrdinalIgnoreCase))return false;
    if(!String.IsNullOrEmpty(users)&&SafetyPolicy.IsWithin(root,users)&&!SafetyPolicy.IsWithin(root,policy.ProfileRoot))return false;
    string normalized=root;
    if(new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)}.Any(p=>!String.IsNullOrEmpty(p)&&String.Equals(normalized,p,StringComparison.OrdinalIgnoreCase)))return false;
    if(new[]{@"C:\System Volume Information",@"C:\$Recycle.Bin",@"C:\Recovery"}.Any(p=>SafetyPolicy.IsWithin(normalized,p)))return false;
    return true;
   }catch(Exception e){if(!ScanWalker.IsFileError(e))throw;return false;}
  }
  public SoftwareInventoryReport Scan(IEnumerable<SoftwareInstall> installed,CancellationToken token,Action<ScanProgress> progress){
   if(installed==null)throw new ArgumentNullException("installed");var report=new SoftwareInventoryReport();var state=new ScanReport();var budget=new InspectionBudget(maximum);var clock=Stopwatch.StartNew();var pulse=new ScanPulse(progress);var global=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   try{
    foreach(var installation in installed){
     token.ThrowIfCancellationRequested();if(installation==null)continue;if(report.Items.Count>=5000){Warn(report,"软件目录清单达到5000项上限。");break;}budget.Step(token);
     var usage=new SoftwareUsage{Installation=installation};report.Items.Add(usage);string root;
     if(!Resolve(installation.InstallLocation,out root)){usage.Incomplete=true;usage.Status="未计量：未登记可核验的C盘软件目录；估算值仅供参考。";report.Incomplete=true;continue;}usage.Root=root;
     long skipped=state.Skipped;var local=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
     try{
      if(!MetadataWalk.ExistsDirectory(root,true,state)){usage.Incomplete=true;usage.Status="目录不存在、无权限或为链接，占用未知。";report.Incomplete=true;continue;}
      usage.Measured=true;
      MetadataWalk.Walk(policy,root,true,true,budget,state,token,delegate(string path,FileStamp stamp){
       string identity=MetadataWalk.Identity(stamp);if(local.Add(identity)){usage.Files++;usage.LogicalBytes+=stamp.Length;}
       if(global.Add(identity)){report.UniqueFiles++;report.UniqueLogicalBytes+=stamp.Length;state.ScannedFiles++;state.LogicalBytes+=stamp.Length;}pulse.Send(installation.Name+" · "+path,state);
      });
     }catch(OperationCanceledException){usage.Incomplete=true;throw;}catch(InspectionLimitException){usage.Incomplete=true;throw;}
     finally{usage.Skipped=state.Skipped-skipped;usage.Incomplete=usage.Incomplete||usage.Skipped>0;if(usage.Incomplete&&usage.Files==0)usage.Measured=false;report.Incomplete=report.Incomplete||usage.Incomplete;if(usage.Status==null)usage.Status=usage.Incomplete?"部分计量：存在无权限、链接、占位或未完成项目。":"只读计量完成；软件目录之间可能重叠。";}
    }
   }catch(OperationCanceledException){report.Cancelled=true;report.Incomplete=true;Warn(report,"软件占用查询已取消，统计只包含已完成部分。");}
   catch(InspectionLimitException){Warn(report,"软件占用查询达到工作量上限，统计不完整。");}
   finally{report.Seconds=clock.Elapsed.TotalSeconds;foreach(string warning in state.Warnings)Warn(report,warning);}
   return report;
  }
 }
}
