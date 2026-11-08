using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ClearDisk {
 internal interface IMigrationUiService {
  MigrationPlan Prepare(string source,string destination,CancellationToken token);
  MigrationExecutionResult Execute(MigrationPlan plan,CancellationToken token,IProgress<MigrationProgress> progress);
  IList<Guid> ListTransactions(CancellationToken token);
  MigrationRecoveryPreview InspectRecovery(Guid transaction,CancellationToken token);
  MigrationExecutionResult Restore(MigrationRecoveryPreview preview,CancellationToken token,IProgress<MigrationProgress> progress);
  MigrationBackupReleasePreview InspectRelease(Guid transaction,CancellationToken token);
  MigrationBackupReleaseResult ReleaseBackup(MigrationBackupReleasePreview preview,CancellationToken token,IProgress<MigrationProgress> progress);
 }
 internal sealed class MigrationUiService:IMigrationUiService {
  readonly MigrationPolicy policy;
  readonly MigrationPlanner planner;
  readonly MigrationExecutor executor;
  readonly MigrationRecovery recovery;
  readonly MigrationBackupRelease release;
  internal MigrationUiService() {
   policy=MigrationPolicy.ForCurrentUser();planner=new MigrationPlanner(policy);
   executor=new MigrationExecutor(policy,planner);recovery=new MigrationRecovery(policy);release=new MigrationBackupRelease(policy);
  }
  public MigrationPlan Prepare(string source,string destination,CancellationToken token) {return planner.Prepare(source,destination,token);}
  public MigrationExecutionResult Execute(MigrationPlan plan,CancellationToken token,IProgress<MigrationProgress> progress) {return executor.Execute(plan,token,progress);}
  public MigrationRecoveryPreview InspectRecovery(Guid transaction,CancellationToken token) {return recovery.InspectRecovery(transaction,token);}
  public MigrationExecutionResult Restore(MigrationRecoveryPreview preview,CancellationToken token,IProgress<MigrationProgress> progress) {return recovery.Restore(preview,token,progress);}
  public MigrationBackupReleasePreview InspectRelease(Guid transaction,CancellationToken token) {return release.Inspect(transaction,token);}
  public MigrationBackupReleaseResult ReleaseBackup(MigrationBackupReleasePreview preview,CancellationToken token,IProgress<MigrationProgress> progress) {return release.Release(preview,token,progress);}
  public IList<Guid> ListTransactions(CancellationToken token) {
   string root=MigrationNativeFiles.Normalize(MigrationJournal.CurrentUserStorage);
   try{File.GetAttributes(root);}catch(DirectoryNotFoundException){return new List<Guid>();}catch(FileNotFoundException){return new List<Guid>();}
   var result=new HashSet<Guid>();int inspected=0;
   using(var parents=new MigrationNativeFiles.ParentLocks(Path.Combine(root,".history-pin")))
   using(var handle=MigrationNativeFiles.OpenStable(root,false)) {
    MigrationNativeFiles.RequireOrdinary(MigrationNativeFiles.Identity(handle,root),true);
    MigrationNativeFiles.RequireNoStreams(root,true);MigrationNativeFiles.RequirePrivateDirectory(root);
    foreach(string path in Directory.EnumerateFiles(root)) {
     token.ThrowIfCancellationRequested();if(++inspected>16000)throw new IOException("迁移记录过多，列表未完整读取。");
     string name=Path.GetFileName(path);if(name.Length!=45||!name.EndsWith(".manifest",StringComparison.Ordinal))continue;
     Guid transaction;int revision;
     if(name[32]!='.'||!Guid.TryParseExact(name.Substring(0,32),"N",out transaction)||transaction==Guid.Empty||!Int32.TryParse(name.Substring(33,3),out revision)||revision<0||revision>=32)continue;
     if(name!=transaction.ToString("N")+"."+revision.ToString("D3",System.Globalization.CultureInfo.InvariantCulture)+".manifest")continue;
     result.Add(transaction);
    }
   }
   // A name only identifies a candidate. InspectRecovery authenticates and checks every live object.
   var candidates=new List<Guid>(result);candidates.Sort();return candidates;
  }
 }
}
