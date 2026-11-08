using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClearDisk {
 internal sealed class StructuredCandidate {
  internal string RuleId,Kind,Path,Detail,RegistryKey,RegistryValue;
 }
 internal interface IStructuredCleanupSource {
  CompatibleOption[] List(CancellationToken token);
  void Read(string[] ids,StructuredScanControl control,CancellationToken token,Action<StructuredCandidate> candidate,Action<string> diagnostic);
 }
 internal interface IStructuredCleanupRevision {string Revision {get;}}
 internal sealed class StructuredScanControl : IDisposable {
  readonly ManualResetEventSlim gate=new ManualResetEventSlim(true);
  internal bool IsPaused {get{return !gate.IsSet;}}
  internal void Pause(){gate.Reset();} internal void Resume(){gate.Set();}
  internal void Checkpoint(CancellationToken token){gate.Wait(token);token.ThrowIfCancellationRequested();}
  public void Dispose(){gate.Dispose();}
 }
 internal sealed class StructuredFileAction {
  internal readonly string RuleId,Kind;
  internal StructuredFileAction(string rule,string kind){RuleId=rule;Kind=kind;}
 }
 internal sealed class StructuredFile {
  internal readonly string Path,Identity,Hash;internal readonly long Bytes;internal readonly bool Executable;
  readonly FileStamp snapshot;readonly StructuredFileAction[] actions;
  internal FileStamp Stamp {get{return new FileStamp{Volume=snapshot.Volume,FileId=snapshot.FileId,Length=snapshot.Length,LastWriteTicks=snapshot.LastWriteTicks,Links=snapshot.Links,FinalPath=snapshot.FinalPath};}}
  internal StructuredFileAction[] Actions {get{return (StructuredFileAction[])actions.Clone();}}
  internal StructuredFile(string path,FileStamp stamp,string hash,bool executable,IEnumerable<StructuredFileAction> attributed){Path=path;snapshot=stamp;Bytes=stamp.Length;Identity=MetadataWalk.Identity(stamp);Hash=hash;Executable=executable;actions=attributed.ToArray();}
 }
 internal sealed class StructuredSpecial {
  internal readonly string RuleId,Kind,Path,Detail;internal readonly StructuredRegistrySnapshot Registry;
  internal StructuredSpecial(StructuredCandidate c,StructuredRegistrySnapshot registry){RuleId=c.RuleId;Kind=c.Kind;Path=c.Path??c.RegistryKey??"";Detail=c.Detail??"";Registry=registry;}
  internal bool Executable {get{return Registry!=null;}}
 }
 internal sealed class StructuredRuleStats {
  internal string RuleId;internal long Files,Bytes,UnavailableFiles;internal int SpecialOperations,UnsupportedOperations,UnknownOperations;
 }
 internal sealed class StructuredCatalog {
  readonly StructuredFile[] files;readonly StructuredSpecial[] specials;readonly string[] diagnostics;
  readonly Dictionary<string,StructuredRuleStats> stats=new Dictionary<string,StructuredRuleStats>(StringComparer.Ordinal);
  internal readonly Guid Id=Guid.NewGuid();internal readonly string Context;internal readonly DateTime CreatedUtc=DateTime.UtcNow;
  internal StructuredFile[] Files {get{return (StructuredFile[])files.Clone();}}internal StructuredSpecial[] Specials {get{return (StructuredSpecial[])specials.Clone();}}internal string[] Diagnostics {get{return (string[])diagnostics.Clone();}}
  internal long LogicalBytes {get{return files.Sum(f=>f.Bytes);}}internal long FileCount {get{return files.LongLength;}}
  internal StructuredCatalog(string context,IEnumerable<StructuredFile> f,IEnumerable<StructuredSpecial> s,IEnumerable<string> d){Context=context;files=f.ToArray();specials=s.ToArray();diagnostics=d.ToArray();foreach(var file in files)foreach(string id in file.Actions.Select(a=>a.RuleId).Distinct(StringComparer.Ordinal)){var value=GetStats(id);value.Files++;value.Bytes+=file.Bytes;if(!file.Executable)value.UnavailableFiles++;}foreach(var special in specials){var value=GetStats(special.RuleId);value.SpecialOperations++;if(!special.Executable)value.UnsupportedOperations++;}foreach(string diagnostic in diagnostics){int split=diagnostic.IndexOf(':');if(split>0)GetStats(diagnostic.Substring(0,split)).UnknownOperations++;}}
  StructuredRuleStats GetStats(string id){StructuredRuleStats value;if(!stats.TryGetValue(id,out value))stats[id]=value=new StructuredRuleStats{RuleId=id};return value;}
  internal StructuredRuleStats Stats(string id){StructuredRuleStats value;if(!stats.TryGetValue(id,out value))return new StructuredRuleStats{RuleId=id};return new StructuredRuleStats{RuleId=id,Files=value.Files,Bytes=value.Bytes,UnavailableFiles=value.UnavailableFiles,SpecialOperations=value.SpecialOperations,UnsupportedOperations=value.UnsupportedOperations,UnknownOperations=value.UnknownOperations};}
 }
 internal sealed class StructuredProgress {
  internal string Message;internal long Files,Bytes;internal int Specials;internal double Seconds;
 }
 internal sealed class StructuredPlan {
  internal readonly StructuredCatalog Catalog;internal readonly string Context;internal readonly DateTime ExpiresUtc;
  internal readonly bool BackupFiles;
  internal readonly bool FilesOnly;
  readonly StructuredFile[] frozenFiles;readonly StructuredSpecial[] frozenSpecials;readonly Dictionary<string,string> fileKinds;
  readonly string[] ids;internal string[] Ids {get{return (string[])ids.Clone();}}
  internal StructuredFile[] Files {get{return (StructuredFile[])frozenFiles.Clone();}}
  internal StructuredSpecial[] Specials {get{return (StructuredSpecial[])frozenSpecials.Clone();}}
  internal long Bytes {get{return Files.Sum(f=>f.Bytes);}}internal long FileCount {get{return Files.LongLength;}}
  internal StructuredPlan(StructuredCatalog catalog,string[] selected,DateTime expires,bool backupFiles):this(catalog,selected,expires,backupFiles,null){}
  internal StructuredPlan(StructuredCatalog catalog,string[] selected,DateTime expires,bool backupFiles,IEnumerable<string> exactFiles):this(catalog,selected,expires,backupFiles,exactFiles,null){}
  internal StructuredPlan(StructuredCatalog catalog,string[] selected,DateTime expires,bool backupFiles,IEnumerable<string> exactFiles,IDictionary<string,string> actions){Catalog=catalog;Context=catalog.Context;ids=(string[])selected.Clone();ExpiresUtc=expires;BackupFiles=backupFiles;FilesOnly=exactFiles!=null;var rules=new HashSet<string>(ids,StringComparer.Ordinal);var identities=FilesOnly?new HashSet<string>(exactFiles,StringComparer.Ordinal):null;frozenFiles=catalog.Files.Where(f=>(identities==null||identities.Contains(f.Identity))&&f.Actions.Any(a=>rules.Contains(a.RuleId))).ToArray();frozenSpecials=FilesOnly?new StructuredSpecial[0]:catalog.Specials.Where(s=>rules.Contains(s.RuleId)).ToArray();if(actions!=null)fileKinds=new Dictionary<string,string>(actions,StringComparer.Ordinal);}
  internal string Kind(StructuredFile file){return fileKinds!=null?fileKinds[file.Identity]:file.Actions.Any(a=>ids.Contains(a.RuleId)&&a.Kind=="Delete")?"Delete":"Truncate";}
 }
 internal sealed class StructuredExecutionItem {
  internal string Path,Kind,Status,Detail,BackupId;internal string[] RuleIds;internal long Bytes;
 }
 internal sealed class StructuredExecutionResult {
  internal readonly List<StructuredExecutionItem> Items=new List<StructuredExecutionItem>();
  internal long FreeBefore,FreeAfter;internal bool Cancelled;
  internal bool Success {get{return !Cancelled&&Items.All(v=>v.Status=="Completed");}}
  internal long CompletedFiles {get{return Items.LongCount(v=>(v.Kind=="Delete"||v.Kind=="Truncate")&&(v.Status=="Completed"||v.Status=="CompletedWithWarning"));}}
  internal long CompletedBytes {get{return Items.Where(v=>v.Status=="Completed"||v.Status=="CompletedWithWarning").Sum(v=>v.Bytes);}}
 }
 internal sealed class StructuredCleanupEngine {
  readonly IStructuredCleanupSource source;readonly StructuredBackupStore backups;readonly Func<string> context;readonly Func<DateTime> clock;
  readonly object sync=new object();readonly HashSet<StructuredCatalog> catalogs=new HashSet<StructuredCatalog>();readonly HashSet<StructuredPlan> plans=new HashSet<StructuredPlan>();CompatibleOption[] options;
  long publicationGeneration;
  readonly Dictionary<string,string> fingerprintCache=new Dictionary<string,string>(StringComparer.Ordinal);
  internal StructuredCleanupEngine(string application):this(new BleachBitStructuredSource(application),new StructuredBackupStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CDriveCleaner","StructuredBackups")),CompatibleCleanupEngine.CurrentContext,()=>DateTime.UtcNow){}
  internal StructuredCleanupEngine(IStructuredCleanupSource input,StructuredBackupStore store,Func<string> identity,Func<DateTime> time){source=input;backups=store;context=identity;clock=time;}
  internal StructuredBackupStore Backups {get{return backups;}}
  internal void ClearFingerprintCache(){lock(sync)fingerprintCache.Clear();}
  string CurrentContext(){var revision=source as IStructuredCleanupRevision;return context()+"|"+(revision==null?"":revision.Revision);}
  internal CompatibleOption[] List(CancellationToken token){long generation;lock(sync){generation=++publicationGeneration;options=null;catalogs.Clear();plans.Clear();}var list=source.List(token);token.ThrowIfCancellationRequested();if(list==null||list.Select(o=>o.Id).Distinct(StringComparer.Ordinal).Count()!=list.Length||list.Any(o=>!CompatibleCleanupEngine.AllowedId(o.Id)))throw new IOException("结构化目录无效。");lock(sync){RequireGeneration(generation);options=(CompatibleOption[])list.Clone();}return (CompatibleOption[])list.Clone();}
  void RequireGeneration(long generation){lock(sync)if(generation!=publicationGeneration)throw new InvalidOperationException("此扫描或目录读取已被较新的操作取代，请使用最新结果。");}
  string[] Validate(IEnumerable<string> selected){CompatibleOption[] available;lock(sync)available=options;if(available==null)throw new InvalidOperationException("先读取规则目录。");var ids=(selected??new string[0]).Distinct(StringComparer.Ordinal).OrderBy(v=>v,StringComparer.Ordinal).ToArray();if(ids.Length==0||ids.Any(id=>!available.Any(o=>o.Id==id)))throw new InvalidOperationException("只能选择目录中的规则。");return ids;}
  internal StructuredCatalog Scan(IEnumerable<string> selected,StructuredScanControl control,CancellationToken token,Action<StructuredProgress> progress){return Scan(selected,control,token,progress,false);}
  internal StructuredCatalog Scan(IEnumerable<string> selected,StructuredScanControl control,CancellationToken token,Action<StructuredProgress> progress,bool forceFreshHash){
   long generation;string[] ids;lock(sync){ids=Validate(selected);generation=++publicationGeneration;catalogs.Clear();plans.Clear();if(forceFreshHash)fingerprintCache.Clear();}
   string before=CurrentContext();var watch=System.Diagnostics.Stopwatch.StartNew();var files=new Dictionary<string,StructuredFile>(StringComparer.Ordinal);var special=new List<StructuredSpecial>();var diagnostics=new List<string>();var seen=new HashSet<string>(StringComparer.Ordinal);control=control??new StructuredScanControl();long totalBytes=0,lastProgress=-100;
   Action<string> diagnostic=s=>{diagnostics.Add(s);};
   source.Read(ids,control,token,c=>{
    control.Checkpoint(token);RequireGeneration(generation);if(c==null||!ids.Contains(c.RuleId))throw new IOException("结构化输出出现未请求的规则。");
    if(c.Kind=="Delete"||c.Kind=="Truncate"){
     try{string path=NativeFiles.Normalize(c.Path);if(backups.Contains(path)||ProtectedArtifact(path))throw new IOException("备份或迁移记录属于保护范围。");
      // Read-only capture issues no mutation authority. Final path and native
      // identity are checked around hashing; execution separately pins ancestors.
      using(var handle=NativeFiles.OpenData(path,false)){
       var info=NativeFiles.Info(handle);if((info.Attributes&0x10)!=0){special.Add(new StructuredSpecial(new StructuredCandidate{RuleId=c.RuleId,Kind="Directory",Path=path,Detail="目录只作单独说明；不会递归删除或删除新增文件。"},null));return;}
       CheckFile(info);var stamp=NativeFiles.Stamp(handle);if(!String.Equals(path,stamp.FinalPath,StringComparison.OrdinalIgnoreCase))throw new IOException("最终路径不一致。");
       string key=MetadataWalk.Identity(stamp),cacheKey=key+":"+stamp.Length+":"+stamp.LastWriteTicks;StructuredFile existing;files.TryGetValue(key,out existing);string hash=null;lock(sync){RequireGeneration(generation);if(!forceFreshHash)fingerprintCache.TryGetValue(cacheKey,out hash);}if(hash==null){hash=StructuredFileIo.Hash(handle,control,token);lock(sync){RequireGeneration(generation);fingerprintCache[cacheKey]=hash;}}
       CheckFile(NativeFiles.Info(handle));var after=NativeFiles.Stamp(handle);if(after.Volume!=stamp.Volume||after.FileId!=stamp.FileId||after.Length!=stamp.Length||after.LastWriteTicks!=stamp.LastWriteTicks||after.Links!=stamp.Links||!String.Equals(after.FinalPath,path,StringComparison.OrdinalIgnoreCase))throw new IOException("读取期间路径、身份或文件状态变化。");
       var action=new StructuredFileAction(c.RuleId,c.Kind);
       if(files.TryGetValue(key,out existing)){
        if(existing.Hash!=hash||existing.Bytes!=stamp.Length||existing.Stamp.LastWriteTicks!=stamp.LastWriteTicks){diagnostic(c.RuleId+": 扫描期间文件变化："+path);files[key]=new StructuredFile(existing.Path,existing.Stamp,existing.Hash,false,existing.Actions.Concat(new[]{action}));}
        else if(!existing.Actions.Any(a=>a.RuleId==c.RuleId&&a.Kind==c.Kind))files[key]=new StructuredFile(existing.Path,existing.Stamp,hash,existing.Executable,existing.Actions.Concat(new[]{action}));
       }else{files.Add(key,new StructuredFile(path,stamp,hash,stamp.Links==1,new[]{action}));totalBytes+=stamp.Length;}
       if(stamp.Links!=1)diagnostic(c.RuleId+": 多硬链接文件仅统计，不执行："+path);
      }
     }catch(Exception e){if(!Expected(e))throw;diagnostic(c.RuleId+": 无法冻结 "+c.Path+"："+e.Message);}
    }else{
     string key=c.RuleId+"\0"+c.Kind+"\0"+c.Path+"\0"+c.RegistryKey+"\0"+c.RegistryValue+"\0"+c.Detail;if(!seen.Add(key))return;
     StructuredRegistrySnapshot registry=null;if(c.Kind=="Winreg")try{registry=StructuredRegistrySnapshot.Capture(c.RegistryKey,c.RegistryValue);}catch(Exception e){if(!Expected(e))throw;diagnostic(c.RuleId+": 注册表快照不可用："+e.Message);}
     special.Add(new StructuredSpecial(c,registry));
    }
    if(progress!=null&&watch.ElapsedMilliseconds-lastProgress>=100){lastProgress=watch.ElapsedMilliseconds;progress(new StructuredProgress{Message=c.RuleId,Files=files.Count,Bytes=totalBytes,Specials=special.Count,Seconds=watch.Elapsed.TotalSeconds});}
   },diagnostic);
   token.ThrowIfCancellationRequested();if(before!=CurrentContext())throw new IOException("用户、权限或规则版本变化，扫描作废。");var result=new StructuredCatalog(before,files.Values,special,diagnostics);lock(sync){RequireGeneration(generation);catalogs.Add(result);}if(progress!=null)progress(new StructuredProgress{Message="文件快照完成",Files=files.Count,Bytes=totalBytes,Specials=special.Count,Seconds=watch.Elapsed.TotalSeconds});return result;
  }
  internal StructuredPlan Preview(StructuredCatalog catalog,IEnumerable<string> selected){return Preview(catalog,selected,true);}
  internal StructuredPlan Preview(StructuredCatalog catalog,IEnumerable<string> selected,bool backupFiles){string[] ids=Validate(selected);lock(sync){if(catalog==null||!catalogs.Contains(catalog)||catalog.Context!=CurrentContext())throw new InvalidOperationException("扫描不属于当前会话，或权限/规则版本已变化。");var plan=new StructuredPlan(catalog,ids,clock().AddMinutes(15),backupFiles);plans.Add(plan);return plan;}}
  internal StructuredPlan PreviewFiles(StructuredCatalog catalog,IEnumerable<string> identities,bool backupFiles){return PreviewFiles(catalog,identities,backupFiles,null);}
  internal StructuredPlan PreviewFiles(StructuredCatalog catalog,IEnumerable<string> identities,bool backupFiles,IDictionary<string,string> actions){lock(sync){
   if(catalog==null||!catalogs.Contains(catalog)||catalog.Context!=CurrentContext())throw new InvalidOperationException("文件清单不属于当前有效扫描，请重新扫描。");
   var requested=new HashSet<string>(identities??new string[0],StringComparer.Ordinal);var files=catalog.Files.Where(f=>requested.Contains(f.Identity)).ToArray();if(requested.Count==0||files.Length!=requested.Count||files.Any(f=>!f.Executable))throw new InvalidOperationException("只能选择本次扫描中可处理的普通文件。");
   var kinds=actions==null?files.ToDictionary(f=>f.Identity,f=>f.Actions.Any(a=>a.Kind=="Delete")?"Delete":"Truncate",StringComparer.Ordinal):new Dictionary<string,string>(actions,StringComparer.Ordinal);if(kinds.Count!=requested.Count||files.Any(f=>!kinds.ContainsKey(f.Identity)||!f.Actions.Any(a=>a.Kind==kinds[f.Identity])))throw new InvalidOperationException("逐文件动作必须属于本次扫描，不能扩大所选处理方式。");
   string[] ids=Validate(files.SelectMany(f=>f.Actions.Where(a=>a.Kind==kinds[f.Identity]).Select(a=>a.RuleId)));var plan=new StructuredPlan(catalog,ids,clock().AddMinutes(15),backupFiles,requested,kinds);plans.Clear();plans.Add(plan);return plan;
  }}
  internal StructuredExecutionResult Execute(StructuredPlan plan,bool confirmed,StructuredScanControl control,CancellationToken token,Action<StructuredProgress> progress){
   if(!confirmed)throw new InvalidOperationException("尚未确认所选冻结清单。");lock(sync){if(plan==null||!plans.Remove(plan)||clock()>plan.ExpiresUtc||plan.Context!=CurrentContext())throw new InvalidOperationException("预览已失效，请重新选择并预览。");}
   var result=new StructuredExecutionResult{FreeBefore=Free()};control=control??new StructuredScanControl();var watch=System.Diagnostics.Stopwatch.StartNew();long completedFiles=0,completedBytes=0,lastExecutionProgress=-100;var selectedIds=new HashSet<string>(plan.Ids,StringComparer.Ordinal);
   foreach(var file in plan.Files){bool mutated=false;var item=new StructuredExecutionItem{Path=file.Path,Kind=plan.Kind(file),RuleIds=file.Actions.Where(a=>selectedIds.Contains(a.RuleId)).Select(a=>a.RuleId).Distinct().ToArray(),Bytes=file.Bytes};result.Items.Add(item);
    try{control.Checkpoint(token);if(!plan.Context.StartsWith(context()+"|",StringComparison.Ordinal))throw new IOException("权限变化，停止执行。");if(!file.Executable)throw new IOException("扫描期间变化或多硬链接文件不可执行。");
     using(var pins=new NativeFiles.ParentLocks(file.Path))using(var handle=StructuredFileIo.OpenMutation(file.Path,item.Kind=="Truncate")){
      Verify(handle,file,token);item.BackupId=plan.BackupFiles?backups.SaveFile(handle,file,item.Kind):backups.JournalFile(file,item.Kind);token.ThrowIfCancellationRequested();
      if(item.Kind=="Delete"){NativeFiles.Delete(handle);mutated=true;}else{StructuredFileIo.Truncate(handle);mutated=true;if(plan.BackupFiles)backups.MarkTruncated(item.BackupId,NativeFiles.Stamp(handle));}item.Status="Completed";completedFiles++;completedBytes+=item.Bytes;item.Detail=plan.BackupFiles?"已按冻结身份和内容执行；备份保留在 C 盘。":"已按冻结清单永久清理；按所选策略未备份内容，仅保留审计记录。";
     }
    }catch(OperationCanceledException){item.Status="Cancelled";item.Detail="已取消；已完成项目的备份保留。";result.Cancelled=true;break;}catch(Exception e){if(!Expected(e))throw;item.Status=mutated?"CompletedWithWarning":"Skipped";item.Detail=mutated?"文件已修改，但后置恢复状态记录失败；原内容备份仍保留，需单独核验恢复："+e.Message:e.Message;if(mutated){completedFiles++;completedBytes+=item.Bytes;}}
    if(progress!=null&&watch.ElapsedMilliseconds-lastExecutionProgress>=100){lastExecutionProgress=watch.ElapsedMilliseconds;progress(new StructuredProgress{Message=file.Path,Files=completedFiles,Bytes=completedBytes,Seconds=watch.Elapsed.TotalSeconds});}
   }
   if(!result.Cancelled)foreach(var special in plan.Specials){var item=new StructuredExecutionItem{Path=special.Path,Kind=special.Kind,RuleIds=new[]{special.RuleId},Status="Unsupported",Detail="特殊操作未执行："+special.Detail};result.Items.Add(item);if(special.Registry!=null)try{control.Checkpoint(token);if(!plan.Context.StartsWith(context()+"|",StringComparison.Ordinal))throw new IOException("权限变化。");item.BackupId=backups.SaveRegistry(special.Registry);special.Registry.DeleteFrozen();item.Status="Completed";item.Detail="已在注册表事务内核验并删除冻结设置；持久备份可恢复。";}catch(OperationCanceledException){item.Status="Cancelled";result.Cancelled=true;break;}catch(Exception e){if(!Expected(e))throw;item.Status="Skipped";item.Detail=e.Message;}}
   if(!result.Cancelled&&!plan.FilesOnly)foreach(string diagnostic in plan.Catalog.Diagnostics){string rule=plan.Ids.FirstOrDefault(id=>diagnostic.StartsWith(id+":",StringComparison.Ordinal));if((rule!=null&&(diagnostic.Contains(": 无法冻结 ")||diagnostic.Contains(": 引擎预览错误：")))||diagnostic.StartsWith("预览报告 ",StringComparison.Ordinal))result.Items.Add(new StructuredExecutionItem{Path="",Kind="Diagnostic",Status="Unavailable",RuleIds=rule==null?new string[0]:new[]{rule},Detail=diagnostic});}
   if(progress!=null)progress(new StructuredProgress{Message="冻结清单处理结束",Files=completedFiles,Bytes=completedBytes,Seconds=watch.Elapsed.TotalSeconds});result.FreeAfter=Free();return result;
  }
  static long Free(){try{return new DriveInfo("C").AvailableFreeSpace;}catch(IOException){return -1;}}
  internal static bool ProtectedArtifact(string path){return path.Split('\\').Any(p=>p.StartsWith(".cdm-",StringComparison.OrdinalIgnoreCase)||p.Equals(".CDriveCleanerMigrationV1",StringComparison.OrdinalIgnoreCase));}
  internal static bool Expected(Exception e){return e is IOException||e is UnauthorizedAccessException||e is System.Security.SecurityException||e is Win32Exception||e is CryptographicException||e is ArgumentException||e is NotSupportedException;}
  internal static void CheckFile(NativeFiles.Information info){if((info.Attributes&(0x400u|0x1000u|0x40000u|0x10u|1u))!=0)throw new IOException("链接、目录、云占位或只读文件不可冻结执行。");}
  internal static void Verify(SafeFileHandle handle,StructuredFile file,CancellationToken token){CheckFile(NativeFiles.Info(handle));var current=NativeFiles.Stamp(handle);var old=file.Stamp;if(current.Volume!=old.Volume||current.FileId!=old.FileId||current.Length!=old.Length||current.LastWriteTicks!=old.LastWriteTicks||current.Links!=1||!String.Equals(current.FinalPath,file.Path,StringComparison.OrdinalIgnoreCase)||NativeFiles.Hash(handle,token)!=file.Hash)throw new IOException("文件身份、链接、大小、时间或内容自扫描后变化，已保留。");}
 }
 internal static class StructuredFileIo {
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr sec,uint creation,uint flags,IntPtr template);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetFilePointerEx(SafeFileHandle handle,long distance,out long result,uint method);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetEndOfFile(SafeFileHandle handle);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool ReadFile(SafeFileHandle handle,byte[] buffer,uint requested,out uint read,IntPtr overlapped);
  internal static SafeFileHandle OpenMutation(string path,bool truncate){if(!truncate)return NativeFiles.OpenData(path,true);var h=CreateFile("\\\\?\\"+NativeFiles.Normalize(path),0xC0010080,0,IntPtr.Zero,3,0x00200000,IntPtr.Zero);if(h.IsInvalid){int error=Marshal.GetLastWin32Error();h.Dispose();throw new Win32Exception(error);}return h;}
  internal static void Truncate(SafeFileHandle h){long pos;if(!SetFilePointerEx(h,0,out pos,0)||!SetEndOfFile(h))throw new Win32Exception(Marshal.GetLastWin32Error());}
  internal static void Copy(SafeFileHandle source,Stream destination){using(var borrowed=new SafeFileHandle(source.DangerousGetHandle(),false))using(var input=new FileStream(borrowed,FileAccess.Read)){input.Position=0;input.CopyTo(destination);}}
  internal static string Hash(SafeFileHandle handle,StructuredScanControl control,CancellationToken token){long position;if(!SetFilePointerEx(handle,0,out position,0))throw new Win32Exception(Marshal.GetLastWin32Error());using(var sha=SHA256.Create()){byte[] buffer=new byte[65536];uint read;while(true){control.Checkpoint(token);if(!ReadFile(handle,buffer,(uint)buffer.Length,out read,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());if(read==0)break;sha.TransformBlock(buffer,0,(int)read,buffer,0);}sha.TransformFinalBlock(new byte[0],0,0);return Convert.ToBase64String(sha.Hash);}}
 }
}
