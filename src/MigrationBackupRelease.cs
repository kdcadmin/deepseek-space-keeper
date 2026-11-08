using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClearDisk {
    // A summary is evidence for a separate confirmation; only the issuing service registers authority.
    internal sealed class MigrationBackupReleasePreview {
        internal readonly Guid Transaction;
        internal readonly string Source,Destination,Backup,Message;
        internal readonly int Files,Directories,PreviouslyMissingFiles,PreviouslyMissingDirectories,Skipped,Unknown;
        internal readonly long Bytes,PreviouslyMissingBytes;
        internal MigrationBackupReleasePreview(Guid transaction,string source,string destination,string backup,int files,long bytes,int missingFiles,long missingBytes,int directories=0,int missingDirectories=0) {
            Transaction=transaction; Source=source; Destination=destination; Backup=backup; Files=files; Bytes=bytes;
            PreviouslyMissingFiles=missingFiles; PreviouslyMissingBytes=missingBytes; Skipped=0; Unknown=0;
            Directories=directories; PreviouslyMissingDirectories=missingDirectories;
            Message="Permanently delete only the verified retained C: backup. The active target remains in use. This invalidates the old restore point, including if interrupted.";
        }
    }
    internal sealed class MigrationBackupReleaseResult {
        internal readonly Guid Transaction;
        internal readonly string Source,Destination,Backup,Message;
        internal readonly bool Completed,CompletionRecorded,Cancelled,ReleaseIntentPersisted,RemainingInventoryVerified,UnconfirmedNativeEffects;
        internal readonly int DeletedFiles,DeletedDirectories,PreviouslyMissingFiles,PreviouslyMissingDirectories,RemainingFiles,RemainingDirectories,Skipped,Unknown,FileDeletionRequests,DirectoryDeletionRequests;
        internal readonly long DeletedBytes,PreviouslyMissingBytes,RemainingBytes;
        internal readonly ulong? FreeBytesBefore,FreeBytesAfter;
        internal decimal? FreeBytesDelta {get {return FreeBytesBefore.HasValue&&FreeBytesAfter.HasValue?(decimal?)((decimal)FreeBytesAfter.Value-(decimal)FreeBytesBefore.Value):null;}}
        internal MigrationBackupReleaseResult(MigrationBackupReleasePreview preview,bool completed,bool recorded,bool cancelled,bool intent,int deletedFiles,long deletedBytes,int remainingFiles,long remainingBytes,int unknown,ulong? before,ulong? after,string message,bool inventoryVerified=false,int fileRequests=0,int directoryRequests=0,int deletedDirectories=0) {
            Transaction=preview.Transaction; Source=preview.Source; Destination=preview.Destination; Backup=preview.Backup;
            Completed=completed; CompletionRecorded=recorded; Cancelled=cancelled; ReleaseIntentPersisted=intent;
            DeletedFiles=deletedFiles; DeletedBytes=deletedBytes; PreviouslyMissingFiles=preview.PreviouslyMissingFiles; PreviouslyMissingBytes=preview.PreviouslyMissingBytes;
            RemainingFiles=remainingFiles; RemainingBytes=remainingBytes; Skipped=remainingFiles; Unknown=unknown;
            FreeBytesBefore=before; FreeBytesAfter=after; Message=message;
            RemainingInventoryVerified=inventoryVerified; FileDeletionRequests=fileRequests; DirectoryDeletionRequests=directoryRequests; DeletedDirectories=deletedDirectories;
            RemainingDirectories=preview.Directories-deletedDirectories; PreviouslyMissingDirectories=preview.PreviouslyMissingDirectories;
            UnconfirmedNativeEffects=fileRequests!=deletedFiles || directoryRequests!=deletedDirectories;
        }
    }
    internal sealed class MigrationBackupRelease {
        sealed class Approval {
            internal readonly string State;
            internal readonly DateTime Expires;
            internal Approval(string state,DateTime expires) {State=state; Expires=expires;}
        }
        // A separately constructed registry cannot issue capabilities into this service's private registry.
        sealed class ApprovalRegistry {
            readonly ConditionalWeakTable<MigrationBackupReleasePreview,Approval> values=new ConditionalWeakTable<MigrationBackupReleasePreview,Approval>();
            internal void Issue(MigrationBackupReleasePreview preview,string state,DateTime expires) {values.Add(preview,new Approval(state,expires));}
            internal Approval Consume(MigrationBackupReleasePreview preview) {
                Approval approval;
                if(preview==null || !values.TryGetValue(preview,out approval) || !values.Remove(preview)) throw new IOException("Backup release preview is forged, foreign or already consumed.");
                if(DateTime.UtcNow>approval.Expires) throw new IOException("Backup release preview expired; inspect again.");
                return approval;
            }
        }
        readonly MigrationPolicy policy;
        readonly ApprovalRegistry approvals=new ApprovalRegistry();
        internal MigrationBackupRelease(MigrationPolicy policy) {if(policy==null) throw new ArgumentNullException("policy"); this.policy=policy;}
        internal MigrationBackupReleasePreview Inspect(Guid transaction,CancellationToken token) {
            token.ThrowIfCancellationRequested();
            using(var concurrency=new MigrationConcurrencyLease()) {
                var store=new MigrationManifestStore(policy); MigrationTransaction tx=store.Load(transaction);
                using(var pins=new ReleasePins(policy,tx,token)) {
                    var preview=new MigrationBackupReleasePreview(tx.Id,tx.Source.Source,tx.Source.Destination,tx.Backup,pins.Backup.Files,pins.Backup.Bytes,pins.Backup.MissingFiles,pins.Backup.MissingBytes,pins.Backup.Directories,pins.Backup.MissingDirectories);
                    approvals.Issue(preview,MigrationManifestStore.StateKey(tx),DateTime.UtcNow.AddMinutes(10)); return preview;
                }
            }
        }
        internal MigrationBackupReleaseResult Release(MigrationBackupReleasePreview preview,CancellationToken token,IProgress<MigrationProgress> progress) {
            Approval approval=approvals.Consume(preview);
            using(var concurrency=new MigrationConcurrencyLease()) {
                int deletedFiles=0,remainingFiles=preview.Files,unknown=-1; long deletedBytes=0,remainingBytes=preview.Bytes;
                bool complete=false,recorded=false,intent=false,cancelled=false; ulong? before=null,after=null;
                BackupLease effects=null;
                string message="Backup release stopped before deleting any tracked data.";
                try {
                    token.ThrowIfCancellationRequested(); var store=new MigrationManifestStore(policy); MigrationTransaction tx=store.Load(preview.Transaction);
                    intent=tx.ReleaseIntent;
                    if(MigrationManifestStore.StateKey(tx)!=approval.State) throw new IOException("Release evidence changed after preview; inspect again.");
                    using(var pins=new ReleasePins(policy,tx,token)) {
                        if(preview.Files!=pins.Backup.Files || preview.Bytes!=pins.Backup.Bytes || preview.PreviouslyMissingFiles!=pins.Backup.MissingFiles || preview.PreviouslyMissingBytes!=pins.Backup.MissingBytes || preview.Directories!=pins.Backup.Directories || preview.PreviouslyMissingDirectories!=pins.Backup.MissingDirectories) throw new IOException("Backup membership changed after preview; inspect again.");
                        effects=pins.Backup;
                        before=MigrationNativeFiles.ProbeVolume(tx.Source.Source).Available;
                        pins.Verify(token); Report(progress,preview,"RevalidatingBackupRelease",deletedFiles,deletedBytes); token.ThrowIfCancellationRequested();
                        if(!tx.ReleaseIntent) {tx.ReleaseIntent=true; store.Save(tx); intent=true;}
                        // Only a successfully flushed intent permits the first deletion. Target data leases survive every deletion.
                        pins.Verify(token);
                        for(int i=0;i<tx.Source.Entries.Count;i++) {
                            token.ThrowIfCancellationRequested(); if(tx.Source.Entries[i].IsDirectory || !pins.Backup.Present(i)) continue;
                            pins.VerifyJunction(); long bytes=tx.Source.Entries[i].Identity.Length;
                            pins.Backup.DeleteFile(i,token);
                            deletedFiles++; deletedBytes=checked(deletedBytes+bytes); remainingFiles--; remainingBytes-=bytes;
                            Report(progress,preview,"DeletingTrackedBackupFile",deletedFiles,deletedBytes);
                        }
                        for(int i=tx.Source.Entries.Count-1;i>=0;i--) {
                            token.ThrowIfCancellationRequested(); if(!tx.Source.Entries[i].IsDirectory || !pins.Backup.Present(i)) continue;
                            pins.VerifyJunction(); pins.Backup.DeleteEmptyDirectory(i,token);
                            Report(progress,preview,"DeletingTrackedEmptyDirectory",deletedFiles,deletedBytes);
                        }
                        pins.Backup.RequireGone(); complete=true; unknown=0;
                        tx.ReleaseCompleted=true; store.Save(tx); recorded=true;
                        message="The verified C: backup was released. The active source junction and target remain in use; the old backup cannot be restored.";
                    }
                } catch(Exception ex) {
                    cancelled=ex is OperationCanceledException || token.IsCancellationRequested;
                    message=complete?"The backup is confirmed absent, but recording completion failed: "+ex.Message:
                        "Release stopped; remaining/changed/unknown data was preserved. Confirmed deletions in this run: "+deletedFiles+" files, "+deletedBytes+" logical bytes. "+ex.Message;
                    if(!complete) message+=" Remaining tracked counts are the preview minus confirmed deletions; the current unknown-member count is unverified. Inspect again before retrying.";
                }
                // Preserve native request/confirmed-effect counters even if a later observation, progress callback or evidence write failed.
                if(effects!=null) {deletedFiles=effects.DeletedFiles; deletedBytes=effects.DeletedBytes; remainingFiles=preview.Files-deletedFiles; remainingBytes=preview.Bytes-deletedBytes;}
                try {after=MigrationNativeFiles.ProbeVolume(preview.Source).Available;} catch(Exception ex) {message+=" Available-space observation failed: "+ex.Message;}
                return new MigrationBackupReleaseResult(preview,complete,recorded,cancelled,intent,deletedFiles,deletedBytes,remainingFiles,remainingBytes,unknown,before,after,message,complete,effects==null?0:effects.FileDeletionRequests,effects==null?0:effects.DirectoryDeletionRequests,effects==null?0:effects.DeletedDirectories);
            }
        }
        static void Report(IProgress<MigrationProgress> progress,MigrationBackupReleasePreview preview,string operation,int files,long bytes) {
            if(progress!=null) progress.Report(new MigrationProgress(MigrationStage.CommittedBackupRetained,operation,bytes,preview.Bytes,files,preview.Files));
        }
        static void RequireEligible(MigrationTransaction tx) {
            if(!tx.Committed || tx.RestoreIntent || tx.Restored || tx.ReleaseCompleted || tx.TargetEntries==null || tx.JunctionIdentity==null) throw new IOException("Only a committed, unrestored transaction with a complete target baseline can release its backup.");
        }
        sealed class ReleasePins : IDisposable {
            readonly MigrationTransaction tx;
            readonly MigrationPolicy policy;
            MigrationNativeFiles.ParentLocks sourceParents,targetParents;
            MigrationTreeLease target;
            SafeFileHandle junction;
            internal BackupLease Backup;
            internal ReleasePins(MigrationPolicy policy,MigrationTransaction tx,CancellationToken token) {
                this.tx=tx; this.policy=policy;
                try {
                    RequireEligible(tx);
                    sourceParents=new MigrationNativeFiles.ParentLocks(tx.Source.Source); targetParents=new MigrationNativeFiles.ParentLocks(tx.Source.Destination);
                    MigrationRuntimeBoundary.ValidateEndpoints(policy,tx.Source,token);
                    MigrationRecovery.AuthenticateJournal(policy,tx);
                    new MigrationJournal(policy,MigrationJournal.CurrentUserStorage,tx.Id,tx.Source.ManifestDigest,tx.Source.Source,tx.Source.Destination).LoadCheckpoint(8);
                    junction=MigrationNativeFiles.OpenStable(tx.Source.Source,true); VerifyJunction();
                    target=new MigrationTreeLease(tx.TargetEntries,tx.Source.Destination,true,token,true);
                    Backup=new BackupLease(tx.Source.Entries,tx.Backup,tx.ReleaseIntent,token);
                    Verify(token);
                } catch {Dispose(); throw;}
            }
            internal void Verify(CancellationToken token) {
                policy.ValidateInstalledPaths(token,DateTime.UtcNow.AddMinutes(15),tx.Source.Source,tx.Source.Destination,tx.Backup,tx.Staging,tx.TemporaryJunction);
                target.Verify(token); VerifyJunction(); Backup.Verify(token);
            }
            internal void VerifyJunction() {MigrationNativeJunction.Verify(junction,tx.Source.Source,MigrationRuntimeBoundary.Rebase(tx.JunctionIdentity,tx.Source.Source),tx.Source.Destination);}
            public void Dispose() {
                if(Backup!=null) {Backup.Dispose(); Backup=null;} if(target!=null) {target.Dispose(); target=null;}
                if(junction!=null) {junction.Dispose(); junction=null;} if(targetParents!=null) {targetParents.Dispose(); targetParents=null;} if(sourceParents!=null) {sourceParents.Dispose(); sourceParents=null;}
            }
        }
        sealed class BackupChangedException : IOException {internal BackupChangedException(string message) : base(message) {}}
        // Private low-level mechanics, never a native-authority factory. Production reaches this only via ReleasePins.
        sealed class BackupLease : IDisposable {
            readonly IList<MigrationEntry> entries;
            readonly SafeFileHandle[] handles;
            readonly string root;
            readonly bool resuming;
            readonly DateTime deadline=DateTime.UtcNow.AddMinutes(15);
            bool changedDirectories;
            internal int Files,Directories,MissingFiles,MissingDirectories,DeletedFiles,DeletedDirectories,FileDeletionRequests,DirectoryDeletionRequests;
            internal long Bytes,MissingBytes,DeletedBytes;
            internal BackupLease(IList<MigrationEntry> entries,string root,bool resuming,CancellationToken token) {
                this.entries=entries; this.root=root; this.resuming=resuming; handles=new SafeFileHandle[entries.Count];
                try {
                    for(int i=0;i<entries.Count;i++) {
                        token.ThrowIfCancellationRequested(); MigrationEntry entry=entries[i]; string path=EntryPath(i);
                        try {handles[i]=entry.IsDirectory?MigrationNativeFiles.OpenStable(path,true):MigrationNativeFiles.OpenForExclusiveTrackedDelete(path,false);}
                        catch(Win32Exception ex) {if(ex.NativeErrorCode!=2 && ex.NativeErrorCode!=3) throw; if(!resuming) throw new BackupChangedException("A tracked backup member is missing before release intent."); MigrationNativeFiles.RequireAbsent(path);}
                        if(handles[i]!=null) {Check(i,token); if(!entry.IsDirectory) {Files++; Bytes=checked(Bytes+entry.Identity.Length);} else Directories++;}
                        else if(!entry.IsDirectory) {MissingFiles++; MissingBytes=checked(MissingBytes+entry.Identity.Length);}
                        else MissingDirectories++;
                    }
                    Verify(token);
                } catch {Dispose(); throw;}
            }
            internal bool Present(int i) {return handles[i]!=null;}
            string EntryPath(int i) {return MigrationRuntimeBoundary.EntryPath(root,entries[i].RelativePath);}
            void Check(int i,CancellationToken token) {
                token.ThrowIfCancellationRequested(); if(DateTime.UtcNow>deadline) throw new IOException("Backup release work budget exhausted.");
                MigrationEntry entry=entries[i]; string path=EntryPath(i); MigrationIdentity now=MigrationNativeFiles.Identity(handles[i],path);
                Match(entry,now,path,entry.IsDirectory&&(resuming||changedDirectories));
                MigrationNativeFiles.RequireOrdinary(now,entry.IsDirectory); MigrationNativeFiles.RequireNoStreams(path,entry.IsDirectory);
                if(!entry.IsDirectory && MigrationNativeFiles.Hash(handles[i],token,deadline)!=entry.Sha256) throw new BackupChangedException("Tracked backup SHA256 changed.");
                // Verify metadata again after hashing through the same stable deletion handle.
                if(!now.Same(MigrationNativeFiles.Identity(handles[i],path))) throw new BackupChangedException("Tracked backup changed during content verification.");
            }
            static void Match(MigrationEntry entry,MigrationIdentity now,string path,bool directoryMayHaveChangedTime) {
                MigrationIdentity expected=MigrationRuntimeBoundary.Rebase(entry.Identity,path);
                if((now.Attributes&FileAttributes.ReadOnly)!=0 || !MigrationManifestStore.SameObject(expected,now) || !expected.FinalPath.Equals(now.FinalPath,StringComparison.OrdinalIgnoreCase) || expected.Attributes!=now.Attributes || expected.Links!=now.Links ||
                    (!directoryMayHaveChangedTime && !expected.Same(now))) throw new BackupChangedException("Tracked backup identity, metadata or readonly status changed.");
            }
            internal void Verify(CancellationToken token) {
                var members=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                for(int i=0;i<entries.Count;i++) if(Present(i) && entries[i].IsDirectory) members.Add(entries[i].RelativePath,new HashSet<string>(StringComparer.Ordinal));
                for(int i=0;i<entries.Count;i++) if(Present(i) && entries[i].RelativePath!="") {
                    HashSet<string> children;
                    if(!members.TryGetValue(Path.GetDirectoryName(entries[i].RelativePath)??"",out children)) throw new BackupChangedException("A present tracked member has a missing parent."); children.Add(EntryPath(i));
                }
                for(int i=0;i<entries.Count;i++) {
                    token.ThrowIfCancellationRequested(); if(!Present(i)) {MigrationNativeFiles.RequireAbsent(EntryPath(i)); continue;}
                    Check(i,token); if(!entries[i].IsDirectory) continue;
                    var actual=new HashSet<string>(StringComparer.Ordinal);
                    foreach(string path in Directory.EnumerateFileSystemEntries(EntryPath(i))) {token.ThrowIfCancellationRequested(); if(actual.Count>=MigrationManifestStore.MaximumEntries || !actual.Add(MigrationNativeFiles.Normalize(path))) throw new BackupChangedException("Backup membership exceeds bounds or contains duplicates.");}
                    if(!actual.SetEquals(members[entries[i].RelativePath])) throw new BackupChangedException("Unknown or changed backup membership; nothing untracked will be deleted.");
                }
            }
            internal void DeleteFile(int i,CancellationToken token) {
                if(entries[i].IsDirectory || !Present(i)) throw new IOException("A present tracked file is required.");
                Check(i,token); token.ThrowIfCancellationRequested();
                MigrationNativeFiles.DeleteTrackedHandle(handles[i],MigrationRuntimeBoundary.Rebase(entries[i].Identity,EntryPath(i)),EntryPath(i));
                FileDeletionRequests++;
                handles[i].Dispose(); handles[i]=null; changedDirectories=true;
                MigrationNativeFiles.RequireAbsent(EntryPath(i));
                DeletedFiles++; DeletedBytes=checked(DeletedBytes+entries[i].Identity.Length);
            }
            internal void DeleteEmptyDirectory(int i,CancellationToken token) {
                if(!entries[i].IsDirectory || !Present(i)) throw new IOException("A present tracked directory is required.");
                Check(i,token); string path=EntryPath(i);
                using(var children=Directory.EnumerateFileSystemEntries(path).GetEnumerator()) if(children.MoveNext()) throw new BackupChangedException("Backup directory is not empty; its remaining contents are preserved.");
                // Own ordinary directory pin denies DELETE. Close it, retain every ancestor, then bind the exact recorded ID again.
                handles[i].Dispose(); handles[i]=null;
                using(var deleting=MigrationNativeFiles.OpenForExclusiveTrackedDelete(path,true)) {
                    MigrationIdentity now=MigrationNativeFiles.Identity(deleting,path); Match(entries[i],now,path,true);
                    MigrationNativeFiles.RequireOrdinary(now,true); MigrationNativeFiles.RequireNoStreams(path,true); token.ThrowIfCancellationRequested();
                    MigrationNativeFiles.DeleteTrackedHandle(deleting,now,path);
                    DirectoryDeletionRequests++;
                }
                changedDirectories=true; MigrationNativeFiles.RequireAbsent(path);
                DeletedDirectories++;
            }
            internal void RequireGone() {MigrationNativeFiles.RequireAbsent(root);}
            public void Dispose() {for(int i=handles.Length-1;i>=0;i--) if(handles[i]!=null) {handles[i].Dispose(); handles[i]=null;}}
        }
    }
}
