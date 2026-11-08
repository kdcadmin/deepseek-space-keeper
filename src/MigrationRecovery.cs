using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClearDisk {
    internal sealed class MigrationRecoveryPreview {
        internal readonly Guid Transaction;
        internal readonly string Source,Destination,Backup,Message;
        internal readonly bool Committed;
        internal readonly int Files;
        internal readonly long Bytes;
        internal MigrationRecoveryPreview(Guid transaction,string source,string destination,string backup,string message,bool committed,int files,long bytes) {Transaction=transaction; Source=source; Destination=destination; Backup=backup; Message=message; Committed=committed; Files=files; Bytes=bytes;}
    }
    internal sealed class MigrationRecovery {
        sealed class Approval {internal readonly string Key; internal Approval(string key) {Key=key;}}
        readonly MigrationPolicy policy;
        readonly ConditionalWeakTable<MigrationRecoveryPreview,Approval> approvals=new ConditionalWeakTable<MigrationRecoveryPreview,Approval>();
        internal MigrationRecovery(MigrationPolicy policy) {if(policy==null) throw new ArgumentNullException("policy"); this.policy=policy;}
        internal MigrationRecoveryPreview InspectRecovery(Guid transaction,CancellationToken token) {
            token.ThrowIfCancellationRequested();
            using(var concurrency=new MigrationConcurrencyLease()) {
                var store=new MigrationManifestStore(policy); MigrationTransaction tx=store.Load(transaction);
                using(var pins=new RecoveryPins(policy,tx,token)) {
                    int files=0; long bytes=0; foreach(MigrationEntry e in tx.Source.Entries) if(!e.IsDirectory) {files++; bytes=checked(bytes+e.Identity.Length);}
                    var preview=new MigrationRecoveryPreview(tx.Id,tx.Source.Source,tx.Source.Destination,tx.Backup,"Restore the verified backup to the original source name. Target/staging data will be retained.",tx.Committed,files,bytes);
                    approvals.Add(preview,new Approval(MigrationManifestStore.StateKey(tx))); return preview;
                }
            }
        }
        internal MigrationExecutionResult Restore(MigrationRecoveryPreview preview,CancellationToken token,IProgress<MigrationProgress> progress) {
            Approval approval;
            if(preview==null || !approvals.TryGetValue(preview,out approval) || !approvals.Remove(preview)) throw new IOException("Recovery preview is forged, foreign or already consumed.");
            using(var concurrency=new MigrationConcurrencyLease()) {
                MigrationTransaction tx=null; bool renamed=false;
                try {
                    token.ThrowIfCancellationRequested(); var store=new MigrationManifestStore(policy); tx=store.Load(preview.Transaction);
                    if(MigrationManifestStore.StateKey(tx)!=approval.Key) throw new IOException("Recovery evidence changed after preview; inspect again.");
                    using(var pins=new RecoveryPins(policy,tx,token)) {
                        Report(progress,tx,"RevalidatingRecovery"); token.ThrowIfCancellationRequested();
                        tx.RestoreIntent=true; store.Save(tx);
                        pins.Verify(token); token.ThrowIfCancellationRequested();
                        // Windows requires closing backup descendant handles before renaming its directory.
                        // Target leases remain held. The backup is re-bound by native root identity below.
                        pins.Backup.Dispose(); pins.Backup=null;
                        using(var backup=MigrationNativeFiles.OpenForRename(tx.Backup)) {
                            MigrationIdentity expected=MigrationRuntimeBoundary.Rebase(tx.Source.Entries[0].Identity,tx.Backup);
                            if(!expected.Same(MigrationNativeFiles.Identity(backup,tx.Backup))) throw new IOException("Backup root changed before restore.");
                            Report(progress,tx,"RestoringSource");
                            pins.RemoveVerifiedJunction(); // Cancellation is deferred until this entry switch completes.
                            MigrationNativeFiles.RequireAbsent(tx.Source.Source);
                            MigrationNativeFiles.RenameNoReplace(backup,expected,tx.Source.Source); renamed=true;
                        }
                    }
                    tx.Restored=true; store.Save(tx); Report(progress,tx,"RestoredSource");
                    return Result(tx,MigrationExecutionStatus.RestoredSource,"The original source name was restored. Target/staging copies remain preserved.",false);
                } catch(Exception ex) {
                    bool cancelled=ex is OperationCanceledException || token.IsCancellationRequested;
                    if(renamed) return Result(tx,MigrationExecutionStatus.RestoredSource,"The original source name was restored, but recording/notification failed: "+ex.Message,cancelled);
                    if(tx!=null) return Result(tx,MigrationExecutionStatus.PreservedConflict,"Recovery stopped; no conflicting source was overwritten. Backup and target/staging remain preserved. "+ex.Message,cancelled);
                    return new MigrationExecutionResult(preview.Transaction,MigrationExecutionStatus.PreservedConflict,preview.Source,preview.Destination,preview.Backup,"","Recovery evidence could not be revalidated: "+ex.Message,cancelled);
                }
            }
        }
        static MigrationExecutionResult Result(MigrationTransaction tx,MigrationExecutionStatus status,string message,bool cancelled) {return new MigrationExecutionResult(tx.Id,status,tx.Source.Source,tx.Source.Destination,tx.Backup,tx.Staging,message,cancelled);}
        static void Report(IProgress<MigrationProgress> progress,MigrationTransaction tx,string operation) {if(progress!=null) progress.Report(new MigrationProgress(tx.Committed?MigrationStage.CommittedBackupRetained:MigrationStage.SourceBackedUp,operation,0,0,0,0));}
        // Private live leases are issued from policy+volumes+full identities+hashes, never from stage numbers alone.
        sealed class RecoveryPins : IDisposable {
            readonly MigrationTransaction tx;
            readonly MigrationPolicy policy;
            MigrationNativeFiles.ParentLocks sourceParents,targetParents;
            internal MigrationTreeLease Backup;
            MigrationTreeLease target;
            SafeFileHandle junction;
            internal RecoveryPins(MigrationPolicy policy,MigrationTransaction tx,CancellationToken token) {
                this.tx=tx; this.policy=policy;
                try {
                    if(tx.ReleaseIntent || tx.Restored) throw new IOException("This backup is no longer an available complete restore point.");
                    sourceParents=new MigrationNativeFiles.ParentLocks(tx.Source.Source); targetParents=new MigrationNativeFiles.ParentLocks(tx.Source.Destination);
                    MigrationRuntimeBoundary.ValidateEndpoints(policy,tx.Source,token); AuthenticateJournal(policy,tx);
                    Backup=new MigrationTreeLease(tx.Source.Entries,tx.Backup,true,token);
                    bool missing=Absent(tx.Source.Source);
                    if(!missing) {
                        if(tx.JunctionIdentity==null) throw new IOException("The source name exists; recovery will not overwrite it.");
                        junction=MigrationNativeFiles.OpenForRename(tx.Source.Source);
                        MigrationNativeJunction.Verify(junction,tx.Source.Source,MigrationRuntimeBoundary.Rebase(tx.JunctionIdentity,tx.Source.Source),tx.Source.Destination);
                    }
                    // An observed temporary junction means publication may have occurred, even without stage 8.
                    if(tx.Committed || tx.JunctionIdentity!=null) {
                        if(tx.TargetEntries==null) throw new IOException("No verified target baseline exists for post-publication recovery.");
                        target=new MigrationTreeLease(tx.TargetEntries,tx.Source.Destination,true,token,true);
                    }
                    Verify(token);
                } catch {Dispose(); throw;}
            }
            internal void Verify(CancellationToken token) {
                policy.ValidateInstalledPaths(token,DateTime.UtcNow.AddMinutes(15),tx.Source.Source,tx.Source.Destination,tx.Backup,tx.Staging,tx.TemporaryJunction);
                Backup.Verify(token); if(target!=null) target.Verify(token);
                if(junction!=null) MigrationNativeJunction.Verify(junction,tx.Source.Source,MigrationRuntimeBoundary.Rebase(tx.JunctionIdentity,tx.Source.Source),tx.Source.Destination);
                else MigrationNativeFiles.RequireAbsent(tx.Source.Source);
            }
            internal void RemoveVerifiedJunction() {
                if(junction==null) return;
                MigrationNativeJunction.Delete(junction,tx.Source.Source,MigrationRuntimeBoundary.Rebase(tx.JunctionIdentity,tx.Source.Source),tx.Source.Destination); junction.Dispose(); junction=null;
            }
            public void Dispose() {if(junction!=null) {junction.Dispose(); junction=null;} if(target!=null) {target.Dispose(); target=null;} if(Backup!=null) {Backup.Dispose(); Backup=null;} if(targetParents!=null) {targetParents.Dispose(); targetParents=null;} if(sourceParents!=null) {sourceParents.Dispose(); sourceParents=null;}}
        }
        internal static void AuthenticateJournal(MigrationPolicy policy,MigrationTransaction tx) {
            var journal=new MigrationJournal(policy,MigrationJournal.CurrentUserStorage,tx.Id,tx.Source.ManifestDigest,tx.Source.Source,tx.Source.Destination);
            int highest=-1;
            int inspected=0; string prefix=tx.Id.ToString("N")+".";
            foreach(string path in Directory.EnumerateFileSystemEntries(MigrationJournal.CurrentUserStorage)) {
                if(++inspected>16000) throw new IOException("Checkpoint discovery budget exhausted."); string name=Path.GetFileName(path);
                if(!name.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".checkpoint",StringComparison.OrdinalIgnoreCase)) continue;
                int stage; if(name.Length!=47 || !Int32.TryParse(name.Substring(33,3),out stage) || stage<0 || stage>8 || name!=prefix+stage.ToString("D3",System.Globalization.CultureInfo.InvariantCulture)+".checkpoint") throw new IOException("Invalid checkpoint record filename.");
                if(stage>highest) highest=stage;
            }
            if(highest<1) throw new IOException("No authenticated source-rename intent exists."); journal.LoadCheckpoint(highest);
        }
        static bool Absent(string path) {try {MigrationNativeFiles.RequireAbsent(path); return true;} catch(IOException) {return false;}}
    }
}
