using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClearDisk {
    internal enum MigrationExecutionStatus {CommittedBackupRetained,RestoredSource,CancelledSourceRestored,FailedSourceRestored,PreservedConflict,RejectedNoMutation}
    internal sealed class MigrationExecutionResult {
        internal readonly Guid Transaction;
        internal readonly MigrationExecutionStatus Status;
        internal readonly string Source,Destination,Backup,Staging,Message;
        internal readonly bool Cancelled;
        internal MigrationExecutionResult(Guid transaction,MigrationExecutionStatus status,string source,string destination,string backup,string staging,string message,bool cancelled) {Transaction=transaction; Status=status; Source=source; Destination=destination; Backup=backup; Staging=staging; Message=message; Cancelled=cancelled;}
    }
    internal sealed class MigrationProgress {
        internal readonly MigrationStage Stage;
        internal readonly string Operation;
        internal readonly long Bytes,TotalBytes;
        internal readonly int Files,TotalFiles;
        internal MigrationProgress(MigrationStage stage,string operation,long bytes,long totalBytes,int files,int totalFiles) {Stage=stage; Operation=operation; Bytes=bytes; TotalBytes=totalBytes; Files=files; TotalFiles=totalFiles;}
    }
    internal enum MigrationAttemptRestore {Unchanged,Restored,Conflict}
    internal interface IMigrationExecutionEffects {
        void Prepare(); void Checkpoint(MigrationStage stage); void RenameSource(); void Copy(); void PublishTarget(); void PublishJunction(); void Commit(); MigrationAttemptRestore TryRestore();
    }
    internal sealed class MigrationProtocolResult {
        internal readonly bool Committed,Cancelled;
        internal readonly MigrationAttemptRestore Restore;
        internal readonly Exception Failure;
        internal readonly string Operation;
        internal MigrationProtocolResult(bool committed,bool cancelled,MigrationAttemptRestore restore,Exception failure,string operation) {Committed=committed; Cancelled=cancelled; Restore=restore; Failure=failure; Operation=operation;}
    }
    // The pure protocol has no path, plan, volume or native effect factory. Test adapters cannot mint native authority.
    internal static class MigrationExecutionProtocol {
        internal static MigrationProtocolResult Run(IMigrationExecutionEffects effects,CancellationToken token,Action<string> fault) {
            bool critical=false;
            string operation="Starting";
            Action<string> boundary=delegate(string point) {operation=point; if(fault!=null) fault(point); if(!critical) token.ThrowIfCancellationRequested();};
            Action<MigrationStage> checkpoint=delegate(MigrationStage stage) {boundary("BeforeCheckpoint"+(int)stage); operation="Checkpoint"+(int)stage; effects.Checkpoint(stage); boundary("AfterCheckpoint"+(int)stage);};
            Action<string,Action> effect=delegate(string name,Action action) {boundary("Before"+name); operation=name; action(); boundary("After"+name);};
            try {
                effect("Prepare",effects.Prepare); checkpoint(MigrationStage.Prepared); checkpoint(MigrationStage.SourceRenameIntent);
                effect("RenameSource",effects.RenameSource); checkpoint(MigrationStage.SourceBackedUp); checkpoint(MigrationStage.CopyIntent);
                effect("Copy",effects.Copy); checkpoint(MigrationStage.CopyVerified); checkpoint(MigrationStage.PublishIntent);
                effect("PublishTarget",effects.PublishTarget); checkpoint(MigrationStage.TargetPublished); checkpoint(MigrationStage.JunctionIntent);
                boundary("BeforePublishJunction"); critical=true; operation="PublishJunction"; effects.PublishJunction(); boundary("AfterPublishJunction");
                effect("Commit",effects.Commit); checkpoint(MigrationStage.CommittedBackupRetained);
                return new MigrationProtocolResult(true,false,MigrationAttemptRestore.Unchanged,null,operation);
            } catch(Exception ex) {
                MigrationAttemptRestore restored=MigrationAttemptRestore.Conflict;
                try {restored=effects.TryRestore();} catch(Exception restoreFailure) {ex=new AggregateException("Migration stopped and source restoration could not be proven safe.",ex,restoreFailure);}
                return new MigrationProtocolResult(false,ex is OperationCanceledException || token.IsCancellationRequested,restored,ex,operation);
            }
        }
    }
    internal sealed class MigrationConcurrencyLease : IDisposable {
        static int active;
        readonly Mutex mutex;
        bool owned;
        int disposed;
        internal MigrationConcurrencyLease() {
            if(Interlocked.CompareExchange(ref active,1,0)!=0) throw new IOException("Another migration operation is active in this process.");
            try {
                SecurityIdentifier sid=WindowsIdentity.GetCurrent().User; var security=new MutexSecurity(); security.SetAccessRuleProtection(true,false);
                security.AddAccessRule(new MutexAccessRule(sid,MutexRights.FullControl,AccessControlType.Allow));
                security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),MutexRights.FullControl,AccessControlType.Allow));
                bool created; mutex=new Mutex(false,"Global\\ClearDisk.Migration."+sid.Value,out created,security);
                try {owned=mutex.WaitOne(0,false);} catch(AbandonedMutexException ex) {owned=true; throw new IOException("A migration process ended unexpectedly; inspect its transaction before retrying.",ex);}
                if(!owned) throw new IOException("Another migration process is active for this user.");
            } catch {Dispose(); throw;}
        }
        public void Dispose() {if(Interlocked.Exchange(ref disposed,1)!=0) return; if(mutex!=null) {if(owned) {mutex.ReleaseMutex(); owned=false;} mutex.Dispose();} Interlocked.Exchange(ref active,0);}
    }
    internal static class MigrationRuntimeBoundary {
        internal static MigrationIdentity Rebase(MigrationIdentity identity,string path) {return new MigrationIdentity(identity.VolumeGuid,identity.VolumeSerial,identity.FileId,path,identity.Attributes,identity.LastWriteFileTime,identity.Length,identity.Links);}
        internal static string EntryPath(string root,string relative) {return relative==""?root:Path.Combine(root,relative);}
        internal static void SameParent(MigrationIdentity expected,string path) {
            using(var handle=MigrationNativeFiles.OpenStable(path,false)) {
                MigrationIdentity now=MigrationNativeFiles.Identity(handle,path); MigrationNativeFiles.RequireOrdinary(now,true); MigrationNativeFiles.RequireNoStreams(path,true);
                if(!MigrationManifestStore.SameObject(expected,now)) throw new IOException("Endpoint parent identity changed.");
            }
        }
        internal static void Volume(MigrationVolume expected,string path) {MigrationVolume now=MigrationNativeFiles.ProbeVolume(path); if(now.Guid!=expected.Guid || now.Serial!=expected.Serial) throw new IOException("The endpoint drive now maps to a different actual volume.");}
        internal static void ValidateEndpoints(MigrationPolicy policy,MigrationSnapshot snapshot,CancellationToken token) {
            policy.ValidateInstalledEndpoints(snapshot.Source,snapshot.Destination,token,DateTime.UtcNow.AddMinutes(15));
            policy.ValidateSourcePath(snapshot.Source); policy.ValidateDestinationPath(snapshot.Destination);
            if(snapshot.IsSimulation || snapshot.SourceVolume.Guid.Equals(snapshot.DestinationVolume.Guid,StringComparison.OrdinalIgnoreCase)) throw new IOException("A genuine different-volume plan is required.");
            Volume(snapshot.SourceVolume,snapshot.Source); Volume(snapshot.DestinationVolume,snapshot.Destination);
            SameParent(snapshot.SourceParent,Path.GetDirectoryName(snapshot.Source)); SameParent(snapshot.DestinationParent,Path.GetDirectoryName(snapshot.Destination));
            DateTime deadline=DateTime.UtcNow.AddMinutes(15); policy.ValidateProjectAncestors(Path.GetDirectoryName(snapshot.Source),token,deadline);
            for(string path=Path.GetDirectoryName(snapshot.Destination);path!=null && path.Length>3;path=Path.GetDirectoryName(path)) MigrationPolicy.RequireNoProjectMarkers(path,token,deadline);
            const FileAttributes supported=FileAttributes.Directory|FileAttributes.Archive|FileAttributes.ReadOnly|FileAttributes.Hidden|FileAttributes.Normal|FileAttributes.NotContentIndexed;
            foreach(MigrationEntry entry in snapshot.Entries) {token.ThrowIfCancellationRequested(); policy.ValidateEntry(entry.Identity.FinalPath,entry.IsDirectory); if((entry.Identity.Attributes&~supported)!=0) throw new IOException("This migration cannot preserve one of the source attributes.");}
        }
        internal static void RequireSpace(MigrationSnapshot snapshot) {
            long bytes=0; foreach(MigrationEntry e in snapshot.Entries) if(!e.IsDirectory) bytes=checked(bytes+e.Identity.Length);
            ulong required=checked((ulong)bytes+(ulong)Math.Max(64L*1024*1024,bytes/20)+(ulong)snapshot.Entries.Count*4096);
            if(MigrationNativeFiles.ProbeVolume(snapshot.Destination).Available<required) throw new IOException("Insufficient target free space including migration overhead.");
        }
    }
    // File data handles deny ordinary writes; directory data pins deny DELETE/rename but permit child writes.
    // These leases do NOT freeze tree membership or mapped writers.
    internal sealed class MigrationTreeLease : IDisposable {
        readonly IList<MigrationEntry> baseline;
        readonly List<SafeFileHandle> handles=new List<SafeFileHandle>();
        readonly List<MigrationEntry> observed=new List<MigrationEntry>();
        readonly string root;
        readonly bool sameObjects;
        readonly bool privateDirectories;
        readonly DateTime deadline=DateTime.UtcNow.AddMinutes(15);
        internal IList<MigrationEntry> Entries {get {return observed.AsReadOnly();}}
        internal SafeFileHandle Handle(int index) {return handles[index];}
        internal MigrationTreeLease(IList<MigrationEntry> expected,string root,bool sameObjects,CancellationToken token,bool privateDirectories=false) {
            baseline=expected; this.root=root; this.sameObjects=sameObjects; this.privateDirectories=privateDirectories;
            try {
                for(int i=0;i<expected.Count;i++) {
                    token.ThrowIfCancellationRequested(); MigrationEntry entry=expected[i]; string path=MigrationRuntimeBoundary.EntryPath(root,entry.RelativePath);
                    // Directory LIST access makes deny-delete sharing effective even for an empty tree.
                    SafeFileHandle handle=entry.IsDirectory?MigrationNativeFiles.OpenDirectoryPin(path):MigrationNativeFiles.OpenStable(path,true); handles.Add(handle);
                    MigrationIdentity id=MigrationNativeFiles.Identity(handle,path); MigrationNativeFiles.RequireOrdinary(id,entry.IsDirectory); MigrationNativeFiles.RequireNoStreams(path,entry.IsDirectory);
                    string hash=entry.IsDirectory?null:MigrationNativeFiles.Hash(handle,token,deadline);
                    Match(entry,id,hash); observed.Add(new MigrationEntry(entry.RelativePath,id,hash));
                }
                Verify(token);
            } catch {Dispose(); throw;}
        }
        void Match(MigrationEntry expected,MigrationIdentity now,string hash) {
            MigrationIdentity rebased=MigrationRuntimeBoundary.Rebase(expected.Identity,now.FinalPath);
            if(sameObjects?!rebased.Same(now):expected.Identity.Attributes!=now.Attributes || expected.Identity.LastWriteFileTime!=now.LastWriteFileTime || !expected.IsDirectory&&expected.Identity.Length!=now.Length || !expected.IsDirectory&&now.Links!=1) throw new IOException("A tracked file/directory identity or supported metadata changed.");
            if(hash!=expected.Sha256) throw new IOException("A tracked file SHA256 changed.");
        }
        internal void Verify(CancellationToken token) {
            var membership=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach(MigrationEntry entry in baseline) if(entry.IsDirectory) membership.Add(entry.RelativePath,new HashSet<string>(StringComparer.Ordinal));
            foreach(MigrationEntry entry in baseline) if(entry.RelativePath!="") membership[Path.GetDirectoryName(entry.RelativePath)??""].Add(MigrationRuntimeBoundary.EntryPath(root,entry.RelativePath));
            for(int i=0;i<baseline.Count;i++) {
                token.ThrowIfCancellationRequested(); if(DateTime.UtcNow>deadline) throw new IOException("Migration verification budget exhausted."); MigrationEntry entry=baseline[i]; string path=MigrationRuntimeBoundary.EntryPath(root,entry.RelativePath);
                MigrationIdentity id=MigrationNativeFiles.Identity(handles[i],path); MigrationNativeFiles.RequireOrdinary(id,entry.IsDirectory); MigrationNativeFiles.RequireNoStreams(path,entry.IsDirectory);
                Match(entry,id,entry.IsDirectory?null:MigrationNativeFiles.Hash(handles[i],token,deadline));
                if(entry.IsDirectory) {
                    if(privateDirectories) MigrationNativeFiles.RequirePrivateDirectory(path);
                    MigrationPolicy.RequireNoProjectMarkers(path,token,deadline); var children=new HashSet<string>(StringComparer.Ordinal);
                    foreach(string child in Directory.EnumerateFileSystemEntries(path)) {token.ThrowIfCancellationRequested(); if(children.Count>=MigrationManifestStore.MaximumEntries || !children.Add(MigrationNativeFiles.Normalize(child))) throw new IOException("Tree membership budget or duplicate exceeded.");}
                    if(!children.SetEquals(membership[entry.RelativePath])) throw new IOException("Directory membership changed; the entire transaction is rejected.");
                }
            }
        }
        public void Dispose() {for(int i=handles.Count-1;i>=0;i--) handles[i].Dispose(); handles.Clear();}
    }
    internal sealed class MigrationExecutor {
        readonly MigrationPolicy policy;
        readonly MigrationPlanner planner;
        internal MigrationExecutor(MigrationPolicy policy,MigrationPlanner planner) {if(policy==null || planner==null) throw new ArgumentNullException("policy/planner"); this.policy=policy; this.planner=planner;}
        internal MigrationExecutionResult Execute(MigrationPlan plan,CancellationToken token,IProgress<MigrationProgress> progress) {
            // Validate exact issuing-plan capability before even creating a manifest-storage directory.
            MigrationSnapshot snapshot=planner.GetSnapshotForExecution(plan);
            using(var concurrency=new MigrationConcurrencyLease())
            using(var effects=new NativeEffects(policy,snapshot,token,progress)) {
                MigrationProtocolResult result=MigrationExecutionProtocol.Run(effects,token,null);
                MigrationExecutionStatus status=result.Committed?MigrationExecutionStatus.CommittedBackupRetained:result.Restore==MigrationAttemptRestore.Restored?(result.Cancelled?MigrationExecutionStatus.CancelledSourceRestored:MigrationExecutionStatus.FailedSourceRestored):result.Restore==MigrationAttemptRestore.Unchanged?MigrationExecutionStatus.RejectedNoMutation:MigrationExecutionStatus.PreservedConflict;
                string message=result.Committed?"Migration entry switched; the C: backup is retained and its space has not been released.":result.Restore==MigrationAttemptRestore.Restored?"The original source name was restored. Any staging/target copy remains preserved.":result.Restore==MigrationAttemptRestore.Unchanged?"Migration stopped before the source was moved.":"Migration stopped with data preserved. Inspect recovery; no conflicting source name was overwritten.";
                if(result.Failure!=null) message+=" Phase='"+result.Operation+"'. "+result.Failure.Message;
                MigrationTransaction tx=effects.Transaction;
                return new MigrationExecutionResult(tx.Id,status,snapshot.Source,snapshot.Destination,tx.Backup,tx.Staging,message,result.Cancelled);
            }
        }
        // The only production native effect factory is private to the exact-plan validating executor.
        sealed class NativeEffects : IMigrationExecutionEffects,IDisposable {
            readonly MigrationPolicy policy;
            readonly MigrationSnapshot snapshot;
            readonly CancellationToken token;
            readonly IProgress<MigrationProgress> progress;
            readonly MigrationTransaction tx;
            MigrationManifestStore store;
            MigrationJournal journal;
            MigrationNativeFiles.ParentLocks sourceParents,targetParents;
            bool sourceRenameAttempted,junctionMayBeVisible;
            long copied,total;
            int files,totalFiles;
            readonly DateTime deadline=DateTime.UtcNow.AddMinutes(15);
            internal MigrationTransaction Transaction {get {return tx;}}
            internal NativeEffects(MigrationPolicy policy,MigrationSnapshot snapshot,CancellationToken token,IProgress<MigrationProgress> progress) {
                this.policy=policy; this.snapshot=snapshot; this.token=token; this.progress=progress; tx=new MigrationTransaction(Guid.NewGuid(),snapshot);
                foreach(MigrationEntry e in snapshot.Entries) if(!e.IsDirectory) {total=checked(total+e.Identity.Length); totalFiles++;}
            }
            public void Prepare() {
                sourceParents=new MigrationNativeFiles.ParentLocks(snapshot.Source); targetParents=new MigrationNativeFiles.ParentLocks(snapshot.Destination);
                MigrationRuntimeBoundary.ValidateEndpoints(policy,snapshot,token); MigrationRuntimeBoundary.RequireSpace(snapshot);
                MigrationNativeFiles.RequireAbsent(snapshot.Destination); MigrationNativeFiles.RequireAbsent(tx.Backup); MigrationNativeFiles.RequireAbsent(tx.Staging); MigrationNativeFiles.RequireAbsent(tx.TemporaryJunction);
                using(var source=new MigrationTreeLease(snapshot.Entries,snapshot.Source,true,token)) source.Verify(token);
                store=new MigrationManifestStore(policy); store.Save(tx);
                journal=new MigrationJournal(policy,MigrationJournal.CurrentUserStorage,tx.Id,snapshot.ManifestDigest,snapshot.Source,snapshot.Destination);
            }
            public void Checkpoint(MigrationStage stage) {journal.WriteCheckpoint((int)stage,stage); Report(stage,stage.ToString());}
            public void RenameSource() {
                policy.ValidateInstalledPaths(token,deadline,snapshot.Source,snapshot.Destination,tx.Backup,tx.Staging,tx.TemporaryJunction);
                sourceRenameAttempted=true;
                using(var handle=MigrationNativeFiles.OpenForRename(snapshot.Source)) MigrationNativeFiles.RenameNoReplace(handle,snapshot.Entries[0].Identity,tx.Backup);
                using(var backup=new MigrationTreeLease(snapshot.Entries,tx.Backup,true,token)) backup.Verify(token);
            }
            public void Copy() {
                MigrationRuntimeBoundary.ValidateEndpoints(policy,snapshot,token); MigrationRuntimeBoundary.RequireSpace(snapshot);
                using(var backup=new MigrationTreeLease(snapshot.Entries,tx.Backup,true,token)) {
                    var directories=new Dictionary<string,SafeFileHandle>(StringComparer.OrdinalIgnoreCase); var created=new MigrationEntry[snapshot.Entries.Count];
                    try {
                        SafeFileHandle root=MigrationNativeFiles.CreatePrivateDirectoryAndOpen(tx.Staging); directories.Add("",root);
                        tx.StagingIdentity=MigrationNativeFiles.Identity(root,tx.Staging); created[0]=new MigrationEntry("",tx.StagingIdentity,null);
                        store.Save(tx); // An observed native ID is durable before any child creation.
                        for(int i=1;i<snapshot.Entries.Count;i++) {
                        token.ThrowIfCancellationRequested(); MigrationEntry e=snapshot.Entries[i]; string path=MigrationRuntimeBoundary.EntryPath(tx.Staging,e.RelativePath);
                        string parentRelative=Path.GetDirectoryName(e.RelativePath)??""; SafeFileHandle parent=directories[parentRelative];
                        MigrationIdentity parentIdentity=MigrationNativeFiles.Identity(parent,MigrationRuntimeBoundary.EntryPath(tx.Staging,parentRelative));
                        if(e.IsDirectory) {SafeFileHandle child=MigrationNativeFiles.CreatePrivateDirectoryAndOpen(path,parent,parentIdentity); directories.Add(e.RelativePath,child); created[i]=new MigrationEntry(e.RelativePath,MigrationNativeFiles.Identity(child,path),null);}
                        else {
                            using(var handle=MigrationNativeFiles.CreateNewFile(path,parent,parentIdentity)) {
                                string hash=MigrationNativeFiles.CopyAndFlush(backup.Handle(i),handle,token,deadline);
                                if(hash!=e.Sha256) throw new IOException("Backup bytes changed during copy.");
                                MigrationNativeFiles.ApplySupportedMetadata(handle,e.Identity);
                                if(MigrationNativeFiles.Hash(handle,token,deadline)!=e.Sha256) throw new IOException("Copied file SHA256 readback differs.");
                                created[i]=new MigrationEntry(e.RelativePath,MigrationNativeFiles.Identity(handle,path),e.Sha256);
                            }
                            copied=checked(copied+e.Identity.Length); files++; Report(MigrationStage.CopyIntent,"CopyingAndVerifying");
                        }
                        }
                        // Every directory is still the exclusive handle returned by its atomic creation.
                        for(int i=snapshot.Entries.Count-1;i>=0;i--) if(snapshot.Entries[i].IsDirectory) {
                        token.ThrowIfCancellationRequested(); MigrationEntry e=snapshot.Entries[i];
                            SafeFileHandle handle=directories[e.RelativePath]; MigrationNativeFiles.ApplySupportedMetadata(handle,e.Identity);
                            created[i]=new MigrationEntry(e.RelativePath,MigrationNativeFiles.Identity(handle,MigrationRuntimeBoundary.EntryPath(tx.Staging,e.RelativePath)),null);
                        }
                    } finally {foreach(SafeFileHandle handle in directories.Values) handle.Dispose();}
                    // Path reopen is now verification against recorded CREATED IDs, never adoption of replacements.
                    using(var target=new MigrationTreeLease(created,tx.Staging,true,token,true)) {backup.Verify(token); target.Verify(token); tx.TargetEntries=new List<MigrationEntry>(target.Entries); store.Save(tx);}
                }
            }
            public void PublishTarget() {
                using(var target=new MigrationTreeLease(tx.TargetEntries,tx.Staging,true,token,true)) target.Verify(token);
                MigrationNativeFiles.RequireAbsent(snapshot.Destination);
                using(var handle=MigrationNativeFiles.OpenForRename(tx.Staging)) MigrationNativeFiles.RenameNoReplace(handle,tx.TargetEntries[0].Identity,snapshot.Destination);
                using(var target=new MigrationTreeLease(tx.TargetEntries,snapshot.Destination,true,token,true)) target.Verify(token);
            }
            public void PublishJunction() {
                // Cancellation stops before this method. Once publication might have occurred, retain both copies.
                using(var backup=new MigrationTreeLease(snapshot.Entries,tx.Backup,true,CancellationToken.None))
                using(var target=new MigrationTreeLease(tx.TargetEntries,snapshot.Destination,true,CancellationToken.None,true)) {
                    backup.Verify(CancellationToken.None); target.Verify(CancellationToken.None);
                    MigrationNativeFiles.RequireAbsent(snapshot.Source);
                    tx.JunctionIdentity=MigrationNativeJunction.Create(tx.TemporaryJunction,snapshot.Destination); store.Save(tx);
                    using(var link=MigrationNativeFiles.OpenForRename(tx.TemporaryJunction)) {
                        MigrationNativeJunction.Verify(link,tx.TemporaryJunction,tx.JunctionIdentity,snapshot.Destination);
                        junctionMayBeVisible=true;
                        MigrationNativeFiles.RenameNoReplace(link,tx.JunctionIdentity,snapshot.Source);
                    }
                    using(var link=MigrationNativeFiles.OpenStable(snapshot.Source,false)) MigrationNativeJunction.Verify(link,snapshot.Source,MigrationRuntimeBoundary.Rebase(tx.JunctionIdentity,snapshot.Source),snapshot.Destination);
                    backup.Verify(CancellationToken.None); target.Verify(CancellationToken.None);
                }
            }
            public void Commit() {
                using(var backup=new MigrationTreeLease(snapshot.Entries,tx.Backup,true,CancellationToken.None))
                using(var target=new MigrationTreeLease(tx.TargetEntries,snapshot.Destination,true,CancellationToken.None,true))
                using(var link=MigrationNativeFiles.OpenStable(snapshot.Source,false)) {
                    MigrationNativeJunction.Verify(link,snapshot.Source,MigrationRuntimeBoundary.Rebase(tx.JunctionIdentity,snapshot.Source),snapshot.Destination);
                    backup.Verify(CancellationToken.None); target.Verify(CancellationToken.None); tx.Committed=true; store.Save(tx);
                }
            }
            public MigrationAttemptRestore TryRestore() {
                if(!sourceRenameAttempted) return MigrationAttemptRestore.Unchanged;
                if(junctionMayBeVisible) return MigrationAttemptRestore.Conflict;
                if(!Absent(snapshot.Source)) {
                    using(var source=MigrationNativeFiles.OpenStable(snapshot.Source,false)) if(MigrationManifestStore.SameObject(snapshot.Entries[0].Identity,MigrationNativeFiles.Identity(source,snapshot.Source))) return MigrationAttemptRestore.Unchanged;
                    return MigrationAttemptRestore.Conflict;
                }
                MigrationRuntimeBoundary.Volume(snapshot.SourceVolume,tx.Backup); MigrationRuntimeBoundary.SameParent(snapshot.SourceParent,Path.GetDirectoryName(snapshot.Source));
                using(var backup=new MigrationTreeLease(snapshot.Entries,tx.Backup,true,CancellationToken.None)) {
                    backup.Verify(CancellationToken.None); tx.RestoreIntent=true; store.Save(tx);
                    // Windows rejects a directory rename while descendants deny DELETE sharing.
                    // Release verified backup data leases, then bind the exact root again; no tree-freeze promise.
                    backup.Dispose();
                    using(var handle=MigrationNativeFiles.OpenForRename(tx.Backup)) {
                        MigrationIdentity expected=MigrationRuntimeBoundary.Rebase(snapshot.Entries[0].Identity,tx.Backup);
                        MigrationNativeFiles.RequireAbsent(snapshot.Source); MigrationNativeFiles.RenameNoReplace(handle,expected,snapshot.Source);
                    }
                }
                tx.Restored=true; store.Save(tx); return MigrationAttemptRestore.Restored;
            }
            static bool Absent(string path) {try {MigrationNativeFiles.RequireAbsent(path); return true;} catch {return false;}}
            void Report(MigrationStage stage,string operation) {if(progress!=null) progress.Report(new MigrationProgress(stage,operation,copied,total,files,totalFiles));}
            public void Dispose() {if(targetParents!=null) targetParents.Dispose(); if(sourceParents!=null) sourceParents.Dispose();}
        }
    }
}
