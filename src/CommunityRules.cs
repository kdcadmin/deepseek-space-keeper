using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
namespace ClearDisk {
 public sealed class CommunityEntry {
  public string Name, Warning;
  public List<string> FileKeys=new List<string>();
  public List<string> Detect=new List<string>(),DetectFile=new List<string>(),ExcludeKeys=new List<string>(),DetectOS=new List<string>(),SpecialDetect=new List<string>();
  // Preserve every directive for review. Unknown syntax never grants applicability.
  public Dictionary<string,List<string>> Directives=new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);
  public List<string> UnresolvedConditions=new List<string>();
  public int RegistryKeys;
 }
 public sealed class CommunityRules {
  public List<CommunityEntry> Catalog=new List<CommunityEntry>();
  public static CommunityRules LoadBundled() { return Load("ClearDisk.Winapp2.ini"); }
  public static CommunityRules LoadReference() { return Load("ClearDisk.Winapp2.251109.ini"); }
  public static CommunityRules LoadReferenceInventory() {var source=LoadReference();source.Catalog.AddRange(BuiltinSystemReference.Create());return source;}
  static CommunityRules Load(string resource) {
   using(Stream source=Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)) {
    if(source==null) throw new IOException("内置社区规则资源缺失。");
    using(var reader=new StreamReader(source)) return Parse(reader);
   }
  }
  internal static CommunityRules Parse(TextReader reader) {
   var result=new CommunityRules(); CommunityEntry entry=null; string line;
   while((line=reader.ReadLine())!=null) {
    line=line.Trim(); if(line.Length==0 || line[0]==';') continue;
    if(line[0]=='[' && line.EndsWith("]",StringComparison.Ordinal)) { entry=new CommunityEntry{Name=line.Substring(1,line.Length-2),Warning="社区规则仅供查看；可能包含个人文件、账户或应用业务数据。"}; result.Catalog.Add(entry); continue; }
    if(entry==null) continue;
    int equal=line.IndexOf('='); if(equal<1) { entry.UnresolvedConditions.Add(line); continue; }
    string key=line.Substring(0,equal).Trim(),value=line.Substring(equal+1).Trim();
    List<string> values; if(!entry.Directives.TryGetValue(key,out values)) entry.Directives[key]=values=new List<string>(); values.Add(value);
    if(Regex.IsMatch(key,@"^FileKey[0-9]+$",RegexOptions.IgnoreCase)) entry.FileKeys.Add(value);
    else if(Regex.IsMatch(key,@"^RegKey[0-9]+$",RegexOptions.IgnoreCase)) entry.RegistryKeys++;
    else if(Regex.IsMatch(key,@"^Detect[0-9]*$",RegexOptions.IgnoreCase)) entry.Detect.Add(value);
    else if(Regex.IsMatch(key,@"^DetectFile[0-9]*$",RegexOptions.IgnoreCase)) entry.DetectFile.Add(value);
    else if(Regex.IsMatch(key,@"^ExcludeKey[0-9]+$",RegexOptions.IgnoreCase)) entry.ExcludeKeys.Add(value);
    else if(key.Equals("DetectOS",StringComparison.OrdinalIgnoreCase)) entry.DetectOS.Add(value);
    else if(key.Equals("SpecialDetect",StringComparison.OrdinalIgnoreCase)) entry.SpecialDetect.Add(value);
    else if(key.Equals("Warning",StringComparison.OrdinalIgnoreCase)) entry.Warning=value;
    else if(!new[]{"LangSecRef","Section","Default"}.Contains(key,StringComparer.OrdinalIgnoreCase)) entry.UnresolvedConditions.Add(key);
   }
   return result;
  }
 }
 internal interface ICommunityDetectionProbe {
  bool? RegistryExists(string expression);
  Version WindowsVersion { get; }
 }
 internal sealed class CommunityDetectionProbe : ICommunityDetectionProbe {
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
  struct OsInfo { internal uint Size,Major,Minor,Build,Platform; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] internal string ServicePack; }
  [DllImport("ntdll.dll",CharSet=CharSet.Unicode)] static extern int RtlGetVersion(ref OsInfo info);
  public Version WindowsVersion {
   get { try { var info=new OsInfo{Size=(uint)Marshal.SizeOf(typeof(OsInfo))}; return RtlGetVersion(ref info)==0 ? new Version((int)info.Major,(int)info.Minor,(int)info.Build) : null; }
    catch(DllNotFoundException) { return null; } catch(EntryPointNotFoundException) { return null; } }
  }
  internal static bool RegistryPath(string expression,out RegistryHive hive,out string path) {
   hive=RegistryHive.CurrentUser; path=null;
   if(String.IsNullOrWhiteSpace(expression) || expression.Length>4096) return false;
   int split=expression.IndexOf('\\'); if(split<1) return false;
   string prefix=expression.Substring(0,split);
   if(prefix.Equals("HKCU",StringComparison.OrdinalIgnoreCase)) hive=RegistryHive.CurrentUser;
   else if(prefix.Equals("HKLM",StringComparison.OrdinalIgnoreCase)) hive=RegistryHive.LocalMachine;
   else return false;
   path=expression.Substring(split+1);
   return path.Length>0 && path.IndexOfAny("|*?%/:\0".ToCharArray())<0 && path.Split('\\').All(p=>p.Length>0 && p!="." && p!="..");
  }
  public bool? RegistryExists(string expression) {
   RegistryHive hive; string path; if(!RegistryPath(expression,out hive,out path)) return null;
   bool unknown=false;
   foreach(RegistryView view in Environment.Is64BitOperatingSystem ? new[]{RegistryView.Registry64,RegistryView.Registry32} : new[]{RegistryView.Registry32}) {
    try { using(var root=RegistryKey.OpenBaseKey(hive,view)) using(var key=root.OpenSubKey(path,false)) if(key!=null) return true; }
    catch(SecurityException) { unknown=true; } catch(UnauthorizedAccessException) { unknown=true; } catch(IOException) { unknown=true; }
   }
   return unknown ? null : (bool?)false;
  }
 }
 internal sealed class InspectionLimitException : Exception { }
 internal sealed class InspectionBudget {
  int remaining;
  internal InspectionBudget(int maximum) { remaining=Math.Max(0,maximum); }
  internal void Step(CancellationToken token) { token.ThrowIfCancellationRequested(); if(remaining--<=0) throw new InspectionLimitException(); }
 }
 internal static class MetadataWalk {
  // Directory.Exists suppresses access and invalid-path errors. Only a confirmed
  // not-found result is optional absence; all other failures leave occupancy unknown.
  internal static bool ExistsDirectory(string path,bool system,ScanReport report) {
   try {
    FileAttributes attrs=File.GetAttributes(path);
    if((attrs&FileAttributes.Directory)!=0 && (attrs&(FileAttributes.ReparsePoint|FileAttributes.Offline|(system ? 0 : FileAttributes.System)))==0) return true;
    report.Skipped++; ScanWalker.Warn(report,"预期目录不是普通可遍历目录，占用未知："+path); return false;
   } catch(FileNotFoundException) { return false; }
   catch(DirectoryNotFoundException) { return false; }
   catch(Exception e) {
    if(!ScanWalker.IsFileError(e)) throw;
    report.Skipped++; ScanWalker.Warn(report,"目录不可用、无权限或路径无效，占用未知："+path); return false;
   }
  }
  internal static bool PlainDirectory(string path,bool system) {
   FileAttributes a=File.GetAttributes(path);
   return (a&FileAttributes.Directory)!=0 && (a&(FileAttributes.ReparsePoint|FileAttributes.Offline|(system ? 0 : FileAttributes.System)))==0;
  }
  internal static bool AllowedAncestors(SafetyPolicy policy,string directory,bool system) {
   string current=NativeFiles.Normalize(directory);
   if(!system && !SafetyPolicy.IsWithin(current,policy.ProfileRoot)) return false;
   while(current!=null && current.Length>3) {
    if(!PlainDirectory(current,system)) return false;
    if(!system && String.Equals(current,policy.ProfileRoot,StringComparison.OrdinalIgnoreCase)) break;
    current=Path.GetDirectoryName(current);
   }
   return true;
  }
  // Every ancestor is pinned against replacement while each directory is enumerated.
  // A metadata handle verifies final path, link status and cloud attributes again.
  internal static void Walk(SafetyPolicy policy,string root,bool recursive,bool system,InspectionBudget budget,ScanReport report,CancellationToken token,Action<string,FileStamp> visit) {
   var pending=new Stack<string>(); pending.Push(root);
   while(pending.Count>0) {
    budget.Step(token); string dir=pending.Pop();
    try {
     using(var pins=new NativeFiles.ParentLocks(Path.Combine(dir,".metadata"))) {
      if(!AllowedAncestors(policy,dir,system) || (!system && !policy.ValidateInspectionRoot(dir))) { report.Skipped++; ScanWalker.Warn(report,"受保护、链接或离线目录，占用未知："+dir); continue; }
      foreach(string path in Directory.EnumerateFileSystemEntries(dir)) {
       budget.Step(token);
       try {
        FileAttributes attributes=File.GetAttributes(path);
        if((attributes&(FileAttributes.ReparsePoint|FileAttributes.Offline|(system ? 0 : FileAttributes.System)))!=0) { report.Skipped++; continue; }
        if((attributes&FileAttributes.Directory)!=0) { if(recursive) pending.Push(path); continue; }
        if(!system && policy.IsProtectedScanEntry(path,false,true)) { report.Skipped++; continue; }
        using(var handle=NativeFiles.Open(path,false,false)) {
         var info=NativeFiles.Info(handle);
         if((info.Attributes&(0x400u|0x1000u|0x40000u|0x10u|(system ? 0u : 4u)))!=0) { report.Skipped++; continue; }
         var stamp=NativeFiles.Stamp(handle);
         if(!String.Equals(stamp.FinalPath,NativeFiles.Normalize(path),StringComparison.OrdinalIgnoreCase)) { report.Skipped++; continue; }
         visit(path,stamp);
        }
       } catch(Exception e) { if(!ScanWalker.IsFileError(e)) throw; report.Skipped++; }
      }
     }
    } catch(Exception e) { if(!ScanWalker.IsFileError(e)) throw; report.Skipped++; ScanWalker.Warn(report,"目录无权限或已变化，占用未知："+dir); }
   }
  }
  internal static List<string> Children(SafetyPolicy policy,string root,bool system,InspectionBudget budget,ScanReport report,CancellationToken token) {
   var result=new List<string>(); budget.Step(token);
   try {
    using(var pins=new NativeFiles.ParentLocks(Path.Combine(root,".expand"))) {
     if(!AllowedAncestors(policy,root,system) || (!system && !policy.ValidateInspectionRoot(root))) { report.Skipped++; return result; }
     foreach(string path in Directory.EnumerateDirectories(root)) { budget.Step(token); if(ExistsDirectory(path,system,report)) result.Add(path); }
    }
   } catch(Exception e) { if(!ScanWalker.IsFileError(e)) throw; report.Skipped++; ScanWalker.Warn(report,"无法展开目录，占用未知："+root); }
   return result;
  }
  internal static string Identity(FileStamp file) { return file.Volume.ToString("x")+":"+file.FileId.ToString("x"); }
  internal static void Finish(ScanReport report,Stopwatch clock) {
   report.Seconds=clock.Elapsed.TotalSeconds;
   report.Incomplete=report.Incomplete||report.Cancelled||report.Skipped>0;
   if(report.Cancelled) ScanWalker.Warn(report,"扫描已取消；统计与明细仅包含已完成部分。",true);
   if(report.Skipped>0) ScanWalker.Warn(report,"已跳过 "+report.Skipped+" 个受保护、无权限、链接、离线或不支持的项目。占用未知不代表零。",true);
   ScanWalker.Warn(report,"统计为已检查普通文件的逻辑字节数；只读结果不代表可清理空间。",true);
  }
 }
 public sealed class CommunityScanner {
  readonly SafetyPolicy policy; readonly CommunityRules rules; readonly int limit; readonly ICommunityDetectionProbe detection;
  readonly bool referenceInventory;
  string referenceTemp;
  public CommunityScanner(SafetyPolicy safety) : this(safety,CommunityRules.LoadBundled(),500000) { }
  public static CommunityScanner CreateReferenceInventory(SafetyPolicy safety) {
   var source=CommunityRules.LoadReferenceInventory();
   string[] first={"Windows Update *","Windows Temporary Files *","Windows Logs *","NVIDIA Update Files *","Microsoft Visual Studio Installation Packages *"};
   source.Catalog=source.Catalog.OrderBy(e=>{int n=Array.IndexOf(first,e.Name);return n<0?first.Length:n;}).ToList();
   var scanner=new CommunityScanner(safety,source,2000000,new CommunityDetectionProbe(),true);
   scanner.referenceTemp=Path.Combine(safety.ProfileRoot,"AppData","Local","Temp");
   try {
    string configured=Environment.GetEnvironmentVariable("TEMP",EnvironmentVariableTarget.User);
    if(!String.IsNullOrWhiteSpace(configured)) {
     configured=NativeFiles.Normalize(Environment.ExpandEnvironmentVariables(configured));
     if(SafetyPolicy.IsWithin(configured,safety.ProfileRoot)&&!configured.Equals(safety.ProfileRoot,StringComparison.OrdinalIgnoreCase))scanner.referenceTemp=configured;
    }
   }catch(Exception e){if(!ScanWalker.IsFileError(e)&&!(e is SecurityException))throw;}
   return scanner;
  }
  internal static CommunityScanner ReferenceForFixture(SafetyPolicy safety,CommunityRules source){return new CommunityScanner(safety,source,500000,new CommunityDetectionProbe(),true);}
  internal static CommunityScanner ReferenceWithBudgetForFixture(SafetyPolicy safety,CommunityRules source,int budget){return new CommunityScanner(safety,source,budget,new CommunityDetectionProbe(),true);}
  internal CommunityScanner(SafetyPolicy safety,CommunityRules catalog,int workLimit=500000) : this(safety,catalog,workLimit,new CommunityDetectionProbe()) { }
  internal CommunityScanner(SafetyPolicy safety,CommunityRules catalog,int workLimit,ICommunityDetectionProbe probe) : this(safety,catalog,workLimit,probe,false) { }
  CommunityScanner(SafetyPolicy safety,CommunityRules catalog,int workLimit,ICommunityDetectionProbe probe,bool reference) { if(safety==null || catalog==null || probe==null) throw new ArgumentNullException(); policy=safety; rules=catalog; limit=workLimit; detection=probe;referenceInventory=reference; }
  bool ReferencePathAllowed(string path) {
   if(!referenceInventory)return policy.ValidateInspectionRoot(path);
   try { path=NativeFiles.Normalize(path); }catch(Exception e){if(!ScanWalker.IsFileError(e))throw;return false;}
   return new[]{policy.ProfileRoot,Environment.GetFolderPath(Environment.SpecialFolder.Windows),Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),@"C:\PerfLogs",@"C:\Config.msi"}.Any(root=>!String.IsNullOrEmpty(root)&&SafetyPolicy.IsWithin(path,root));
  }
  internal static bool Glob(string name,string pattern) {
   // Winapp2 follows Windows file masks: *.* includes extensionless names.
   if(pattern=="*.*") pattern="*";
   return Regex.IsMatch(name,"\\A"+Regex.Escape(pattern).Replace("\\*",".*").Replace("\\?",".")+"\\z",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
  }
  static bool Part(string part,bool mask) {
   if(String.IsNullOrWhiteSpace(part) || part.Length>255 || part.Any(c=>c<' ') || part=="." || part==".." || part.EndsWith(".",StringComparison.Ordinal) || part.EndsWith(" ",StringComparison.Ordinal)) return false;
   return part.IndexOfAny((mask ? ":\\/|<>\"%" : ":\\/|<>\"%*?").ToCharArray())<0;
  }
  bool Resolve(string expression,out string basis,out string[] pieces) { return Resolve(expression,false,out basis,out pieces); }
  bool Resolve(string expression,bool detection,out string basis,out string[] pieces) {
   basis=null; pieces=null;
   if(String.IsNullOrEmpty(expression) || expression.Length<3 || expression.Length>4096) return false;
   expression=expression.TrimEnd('\\');
   if(expression.Length<3) return false;
   int end=expression.IndexOf('%',1); if(expression[0]!='%' || end<1) return false;
   bool bare=expression.Length==end+1;
   if(!bare&&(expression.Length<=end+2||expression[end+1]!='\\'))return false;
   string variable=expression.Substring(0,end+1);
   if(variable.Equals("%LocalAppData%",StringComparison.OrdinalIgnoreCase)) basis=Path.Combine(policy.ProfileRoot,"AppData","Local");
   else if(variable.Equals("%AppData%",StringComparison.OrdinalIgnoreCase)) basis=Path.Combine(policy.ProfileRoot,"AppData","Roaming");
   else if(variable.Equals("%UserProfile%",StringComparison.OrdinalIgnoreCase)) basis=policy.ProfileRoot;
   else if(variable.Equals("%Documents%",StringComparison.OrdinalIgnoreCase)) basis=Path.Combine(policy.ProfileRoot,"Documents");
   else if(variable.Equals("%LocalLowAppData%",StringComparison.OrdinalIgnoreCase)) basis=Path.Combine(policy.ProfileRoot,"AppData","LocalLow");
   else if(variable.Equals("%Music%",StringComparison.OrdinalIgnoreCase)) basis=Path.Combine(policy.ProfileRoot,"Music");
   else if(variable.Equals("%Pictures%",StringComparison.OrdinalIgnoreCase)) basis=Path.Combine(policy.ProfileRoot,"Pictures");
   else if(variable.Equals("%Video%",StringComparison.OrdinalIgnoreCase)) basis=Path.Combine(policy.ProfileRoot,"Videos");
   else if(variable.Equals("%Temp%",StringComparison.OrdinalIgnoreCase)) {
    try { basis=NativeFiles.Normalize(referenceInventory&&referenceTemp!=null?referenceTemp:Path.GetTempPath()); }
    catch(Exception e) { if(!ScanWalker.IsFileError(e)) throw; return false; }
    if(!SafetyPolicy.IsWithin(basis,policy.ProfileRoot) || basis.Equals(policy.ProfileRoot,StringComparison.OrdinalIgnoreCase)) return false;
   }
   else if(referenceInventory&&variable.Equals("%WinDir%",StringComparison.OrdinalIgnoreCase))basis=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
   else if(referenceInventory&&variable.Equals("%ProgramData%",StringComparison.OrdinalIgnoreCase))basis=Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
   else if(referenceInventory&&variable.Equals("%ProgramFiles%",StringComparison.OrdinalIgnoreCase))basis=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
   else if(referenceInventory&&variable.Equals("%ProgramFiles(x86)%",StringComparison.OrdinalIgnoreCase))basis=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
   else if(referenceInventory&&variable.Equals("%SystemDrive%",StringComparison.OrdinalIgnoreCase))basis=@"C:\";
   else return false;
   if(String.IsNullOrEmpty(basis)||!basis.StartsWith(@"C:\",StringComparison.OrdinalIgnoreCase))return false;
   if(bare){if(!referenceInventory||!variable.Equals("%Temp%",StringComparison.OrdinalIgnoreCase))return false;pieces=new string[0];return ReferencePathAllowed(basis);}
   pieces=expression.Substring(end+2).Split('\\');
   if(pieces.Length>16 || pieces.Any(p=>!Part(p,true))) return false;
   // File roots never expand wildcards directly under a variable. Detection only
   // probes metadata; exclusions compile a pattern without inspecting those paths.
   if(pieces[0].IndexOfAny(new[]{'*','?'})>=0 && !detection&&!referenceInventory) return false;
   if(!detection && basis==policy.ProfileRoot && pieces.Length<2&&!referenceInventory) return false;
   return true;
  }
  IEnumerable<string> Expand(string basis,string[] pieces,InspectionBudget budget,ScanReport report,CancellationToken token) {
   var candidates=new List<string>{basis};
   foreach(string piece in pieces) {
    var next=new List<string>();
    foreach(string parent in candidates) {
     budget.Step(token);
     if(piece.IndexOfAny(new[]{'*','?'})>=0) {
      foreach(string child in MetadataWalk.Children(policy,parent,referenceInventory,budget,report,token)) if(Glob(Path.GetFileName(child),piece)&&ReferencePathAllowed(child)) next.Add(child);
     } else {
      string child=Path.Combine(parent,piece);
      // ParentLocks at each expansion prevents following an intermediate reparse point.
      if(referenceInventory&&!ReferencePathAllowed(child)){report.Skipped++;ScanWalker.Warn(report,"对照范围外目录，未检查："+child);continue;}
      if(MetadataWalk.ExistsDirectory(child,referenceInventory,report)) try {
       using(var pins=new NativeFiles.ParentLocks(Path.Combine(child,".community"))) if(MetadataWalk.AllowedAncestors(policy,child,referenceInventory)&&ReferencePathAllowed(child)) next.Add(child); else report.Skipped++;
      } catch(Exception e) { if(!ScanWalker.IsFileError(e)) throw; report.Skipped++; }
     }
    }
    candidates=next; if(candidates.Count==0) break;
   }
   return candidates;
  }
  void Unresolved(ScanReport report,CommunityEntry entry,string condition) {
   report.Skipped++; ScanWalker.Warn(report,"社区规则条件未解析/不支持，已跳过："+entry.Name+" — "+condition);
  }
  bool? DetectPath(string expression,InspectionBudget budget,ScanReport report,CancellationToken token) {
   string basis; string[] pieces;
   if(!Resolve(expression,true,out basis,out pieces)) return null;
   if(pieces.Length==0)return MetadataWalk.ExistsDirectory(basis,referenceInventory,report);
   string last=pieces[pieces.Length-1]; bool found=false; long skipped=report.Skipped;
   foreach(string root in Expand(basis,pieces.Take(pieces.Length-1).ToArray(),budget,report,token)) {
    budget.Step(token);
    try {
     using(var pins=new NativeFiles.ParentLocks(Path.Combine(root,".detect"))) {
      if(!MetadataWalk.AllowedAncestors(policy,root,referenceInventory) || !ReferencePathAllowed(root)) { report.Skipped++; continue; }
      foreach(string path in Directory.EnumerateFileSystemEntries(root)) {
       budget.Step(token); if(!Glob(Path.GetFileName(path),last)) continue;
       FileAttributes a=File.GetAttributes(path);
       if((a&(FileAttributes.ReparsePoint|FileAttributes.Offline|(referenceInventory?0:FileAttributes.System)))!=0) { report.Skipped++; continue; }
       if((a&FileAttributes.Directory)!=0) { if(ReferencePathAllowed(path)) found=true; else report.Skipped++; }
       else using(var handle=NativeFiles.Open(path,false,false)) {
        var info=NativeFiles.Info(handle);
        if((info.Attributes&(0x400u|0x1000u|0x40000u|0x10u|(referenceInventory?0u:4u)))!=0 || (referenceInventory&&!ReferencePathAllowed(path))||!String.Equals(NativeFiles.Stamp(handle).FinalPath,NativeFiles.Normalize(path),StringComparison.OrdinalIgnoreCase)) { report.Skipped++; continue; }
        found=true;
       }
      }
     }
    } catch(Exception e) { if(!ScanWalker.IsFileError(e)) throw; report.Skipped++; }
   }
   return found ? (bool?)true : report.Skipped>skipped ? null : (bool?)false;
  }
  bool Applicable(CommunityEntry entry,InspectionBudget budget,ScanReport report,CancellationToken token) {
   if(entry.UnresolvedConditions.Count>0) { Unresolved(report,entry,String.Join(", ",entry.UnresolvedConditions)); return false; }
   foreach(string gate in entry.DetectOS) { budget.Step(token); bool? result=CompatibleOS(gate,detection.WindowsVersion); if(result!=true) { if(!result.HasValue) Unresolved(report,entry,"DetectOS="+gate); return false; } }
   bool any=entry.Detect.Count+entry.DetectFile.Count+entry.SpecialDetect.Count>0,found=false,unknown=entry.SpecialDetect.Count>0;
   foreach(string expression in entry.Detect) {
    budget.Step(token); RegistryHive hive; string path;
    bool? result=CommunityDetectionProbe.RegistryPath(expression,out hive,out path) ? detection.RegistryExists(expression) : null;
    if(result==true) found=true; if(!result.HasValue) unknown=true;
   }
   foreach(string expression in entry.DetectFile) { budget.Step(token); bool? result=DetectPath(expression,budget,report,token); if(result==true) found=true; if(!result.HasValue) unknown=true; }
   if(unknown) {
    if(!found) Unresolved(report,entry,"Detect / DetectFile / SpecialDetect");
    else ScanWalker.Warn(report,"部分应用检测条件未解析；已有其他 OR 条件确认适用："+entry.Name+" (Detect / DetectFile / SpecialDetect)");
   }
   return found || !any;
  }
  static bool Bound(string value,out int[] numbers) {
   numbers=null; if(!Regex.IsMatch(value,@"^[0-9]{1,5}\.[0-9]{1,5}(\.[0-9]{1,5}){0,2}$")) return false;
   numbers=value.Split('.').Select(Int32.Parse).ToArray(); return true;
  }
  static int Compare(Version version,int[] bound) {
   int[] actual={version.Major,version.Minor,Math.Max(0,version.Build),Math.Max(0,version.Revision)};
   for(int i=0;i<bound.Length;i++) { int comparison=actual[i].CompareTo(bound[i]); if(comparison!=0) return comparison; } return 0;
  }
  internal static bool? CompatibleOS(string expression,Version version) {
   if(version==null) return null;
   string[] fields=expression.Split('|'); int[] lower=null,upper=null;
   if(fields.Length!=2 || (fields[0].Length==0 && fields[1].Length==0) || (fields[0].Length>0 && !Bound(fields[0],out lower)) || (fields[1].Length>0 && !Bound(fields[1],out upper))) return null;
   if(lower!=null && upper!=null) { var low=new Version(lower[0],lower[1],lower.Length>2 ? lower[2] : 0,lower.Length>3 ? lower[3] : 0); if(Compare(low,upper)>0) return null; }
   return (lower==null || Compare(version,lower)>=0) && (upper==null || Compare(version,upper)<=0);
  }
  sealed class Exclusion { internal Regex Directory; internal string[] Masks; }
  bool Exclusions(CommunityEntry entry,out List<Exclusion> excludes,InspectionBudget budget,ScanReport report,CancellationToken token) {
   excludes=new List<Exclusion>();
   foreach(string value in entry.ExcludeKeys) {
    budget.Step(token);
    string[] fields=value.Split('|');
    if(fields.Length>=2 && fields[0].Equals("REG",StringComparison.OrdinalIgnoreCase)) continue; // No registry actions are executed.
    string basis; string[] pieces;
    if(fields.Length!=3 || (!fields[0].Equals("FILE",StringComparison.OrdinalIgnoreCase) && !fields[0].Equals("PATH",StringComparison.OrdinalIgnoreCase)) || !Resolve(fields[1].TrimEnd('\\'),true,out basis,out pieces)) { Unresolved(report,entry,"ExcludeKey: "+value); return false; }
    string[] masks=fields[2].Split(';'); bool recursive=fields[0].Equals("PATH",StringComparison.OrdinalIgnoreCase);
    if(masks.Length>64 || masks.Any(m=>!Part(m,true) || m.Length>160) || (!recursive && masks.Any(m=>m.IndexOfAny(new[]{'*','?'})>=0))) { Unresolved(report,entry,"ExcludeKey: "+value); return false; }
    string path=Path.Combine(basis,String.Join("\\",pieces));
    string regex="\\A"+Regex.Escape(path).Replace("\\*","[^\\\\]*").Replace("\\?","[^\\\\]")+(recursive ? "(?:\\\\[^\\\\]+)*" : "")+"\\z";
    excludes.Add(new Exclusion{Directory=new Regex(regex,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant),Masks=masks});
   }
   return true;
  }
  public ScanReport Scan(CancellationToken token,Action<ScanProgress> progress) {
   var report=new ScanReport{Mode=referenceInventory?"reference-inventory":"community",Root=referenceInventory?@"C:\":policy.ProfileRoot}; var clock=Stopwatch.StartNew(); var pulse=new ScanPulse(progress);
   var budget=new InspectionBudget(limit); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); int unsupported=0,registry=0;
   try {
    foreach(var entry in rules.Catalog) {
     budget.Step(token); registry+=entry.RegistryKeys;
     if(!Applicable(entry,budget,report,token)) continue;
     List<Exclusion> excludes; if(!Exclusions(entry,out excludes,budget,report,token)) continue;
     var usage=new FolderUsage{Path=entry.Name}; var sectionSeen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
     try {
      foreach(string key in entry.FileKeys) {
       budget.Step(token); string[] fields=key.Split('|'); string basis; string[] pieces;
       if(fields.Length<2 || fields.Length>3 || !Resolve(fields[0],out basis,out pieces)) { unsupported++; continue; }
       string flag=fields.Length==3 ? fields[2].Trim() : "";
       bool recursive=flag.Equals("RECURSE",StringComparison.OrdinalIgnoreCase)||flag.Equals("REMOVESELF",StringComparison.OrdinalIgnoreCase);
       string[] masks=fields[1].Split(';');
       if((flag!="" && !recursive) || masks.Length>64 || masks.Any(m=>!Part(m,true)) || masks.Any(m=>m.Length>160)) { unsupported++; continue; }
       foreach(string root in Expand(basis,pieces,budget,report,token)) {
        if(!ReferencePathAllowed(root)){report.Skipped++;continue;}
        MetadataWalk.Walk(policy,root,recursive,referenceInventory,budget,report,token,delegate(string path,FileStamp stamp) {
         if(!masks.Any(m=>Glob(Path.GetFileName(path),m))) return;
         if(excludes.Any(e=>e.Directory.IsMatch(Path.GetDirectoryName(path)) && e.Masks.Any(m=>Glob(Path.GetFileName(path),m)))) return;
         string identity=MetadataWalk.Identity(stamp);
         if(sectionSeen.Add(identity)) { usage.Files++; usage.Bytes+=stamp.Length; }
         if(!seen.Add(identity)) return;
         report.ScannedFiles++; report.LogicalBytes+=stamp.Length;
         if(report.Files.Count<2000) report.Files.Add(new FileRecord{Path=path,Root=root,RuleId="community",Category=entry.Name,Risk=RiskLevel.Review,Length=stamp.Length,LastWriteTicks=stamp.LastWriteTicks,Reason="社区规则只读匹配。"+entry.Warning});
         pulse.Send(path,report);
        });
       }
      }
     } finally { if(usage.Files>0) report.Folders.Add(usage); }
    }
   } catch(OperationCanceledException) { report.Cancelled=true; }
   catch(InspectionLimitException) { report.Incomplete=true;ScanWalker.Warn(report,"社区扫描达到工作量上限；结果与统计不完整。",true); }
   if(unsupported>0){report.Skipped+=unsupported;report.Incomplete=true;ScanWalker.Warn(report,"已跳过 "+unsupported+" 条不支持的 FileKey 路径、变量、标志或模式；未执行系统或社区清理动作。",true);}
   if(registry>0) ScanWalker.Warn(report,registry+" 条 RegKey 清理定义仅作计数；仅为应用检测只读查询 HKCU/HKLM，不执行任何注册表清理或修改。",true);
   if(report.ScannedFiles>2000) ScanWalker.Warn(report,"文件明细仅显示前 2000 个匹配项；统计包含已完成的全部匹配。",true);
   ScanWalker.Warn(report,"各规则分组统计可能重叠；全局字节数按文件身份去重。仅执行已支持的应用检测与排除条件；未解析条件会跳过并提示。社区结果始终只读，不授权清理或整理。",true);
   if(referenceInventory)ScanWalker.Warn(report,"原版251109与内置System静态路径对照：检查当前用户、Windows、ProgramData和Program Files的规则匹配元数据。内置项仅复现已确认路径；服务/权限条件与原版逐文件运行结果未完全核实。不是可释放空间，系统和安装数据不允许清理；未知或范围外位置会跳过。",true);
   MetadataWalk.Finish(report,clock); return report;
  }
 }
}
