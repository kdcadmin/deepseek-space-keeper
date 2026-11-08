using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClearDisk {
    internal sealed class MigrationPlanner {
        static readonly UTF8Encoding TextEncoding=new UTF8Encoding(false,true);
        readonly MigrationPolicy policy;
        readonly Func<string,MigrationVolume> simulatedProbe;
        readonly ConditionalWeakTable<MigrationPlan,MigrationSnapshot> issued=new ConditionalWeakTable<MigrationPlan,MigrationSnapshot>();
        sealed class OpenEntry {internal MigrationEntry Entry; internal SafeFileHandle Handle; internal string[] Children;}
        internal MigrationPlanner(MigrationPolicy policy) {if(policy==null) throw new ArgumentNullException("policy"); this.policy=policy;}
        // Internal, visibly labelled unit-test seam. A simulated plan is permanently ineligible for execution.
        internal MigrationPlanner(MigrationPolicy policy,Func<string,MigrationVolume> simulatedVolumeProbe) : this(policy) {if(simulatedVolumeProbe==null) throw new ArgumentNullException("simulatedVolumeProbe"); simulatedProbe=simulatedVolumeProbe;}
        internal MigrationPlan Prepare(string source,string destination,CancellationToken token) {return Prepare(source,destination,token,4096,2L*1024*1024*1024*1024,TimeSpan.FromMinutes(15));}
        internal MigrationPlan Prepare(string source,string destination,CancellationToken token,int maxEntries,long maxBytes,TimeSpan maxWork) {
            token.ThrowIfCancellationRequested();
            if(maxEntries<1 || maxEntries>50000 || maxBytes<0 || maxWork<=TimeSpan.Zero || maxWork>TimeSpan.FromHours(1)) throw new MigrationPreflightException("Budget","Invalid migration work limits.",null);
            var open=new List<OpenEntry>();
            try {
                string src=MigrationNativeFiles.Normalize(source),dst=MigrationNativeFiles.Normalize(destination);
                DateTime deadline=DateTime.UtcNow.Add(maxWork);
                policy.ValidateInstalledEndpoints(src,dst,token,deadline);
                policy.ValidateSourcePath(src); if(simulatedProbe==null) policy.ValidateDestinationPath(dst); else policy.ValidateSimulatedDestinationPath(dst);
                if(MigrationPolicy.Within(src,dst) || MigrationPolicy.Within(dst,src)) throw new IOException("Source and destination overlap.");
                using(var sourceParents=new MigrationNativeFiles.ParentLocks(src))
                using(var destinationParents=new MigrationNativeFiles.ParentLocks(dst)) {
                    MigrationNativeFiles.RequireAbsent(dst);
                    string targetParent=Path.GetDirectoryName(dst);
                    string sourceParentPath=Path.GetDirectoryName(src);
                    using(var parentHandle=MigrationNativeFiles.OpenStable(targetParent,false))
                    using(var sourceParentHandle=MigrationNativeFiles.OpenStable(sourceParentPath,false)) {
                        MigrationIdentity targetParentIdentity=MigrationNativeFiles.Identity(parentHandle,targetParent),sourceParentIdentity=MigrationNativeFiles.Identity(sourceParentHandle,sourceParentPath);
                        MigrationNativeFiles.RequireOrdinary(targetParentIdentity,true); MigrationNativeFiles.RequireNoStreams(targetParent,true);
                        // Native pins always verify real volumes, even in the fake-volume unit tests.
                        MigrationVolume actualSourceVolume=MigrationNativeFiles.ProbeVolume(src),sourceVolume=actualSourceVolume,destinationVolume=MigrationNativeFiles.ProbeVolume(dst);
                        if(simulatedProbe!=null) {sourceVolume=simulatedProbe(src); destinationVolume=simulatedProbe(dst);}
                        if(sourceVolume.Guid.Equals(destinationVolume.Guid,StringComparison.OrdinalIgnoreCase)) throw new IOException("Source and target must be on different actual volumes.");
                        for(string ancestor=targetParent;ancestor!=null && ancestor.Length>3;ancestor=Path.GetDirectoryName(ancestor)) {
                            MigrationPolicy.RequireNoProjectMarkers(ancestor,token,deadline);
                            if(simulatedProbe!=null && ancestor.Equals(policy.ProfileRoot,StringComparison.OrdinalIgnoreCase)) break;
                        }
                        var pending=new Stack<string>(); pending.Push(src); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var manifest=new List<MigrationEntry>();
                        long bytes=0; int files=0,dirs=0;
                        while(pending.Count>0) {
                            CheckWork(token,deadline); string path=pending.Pop();
                            if(manifest.Count>=maxEntries || !seen.Add(path)) throw new IOException("Entry budget or duplicate-case migration path rejected.");
                            string relative=path.Equals(src,StringComparison.OrdinalIgnoreCase)?"":path.Substring(src.Length+1);
                            if(relative.Length>0) foreach(string part in relative.Split('\\')) MigrationNativeFiles.ValidateComponent(part);
                            // Attributes are a hint only; authoritative data comes from the stable handle.
                            bool directory=(File.GetAttributes(path)&FileAttributes.Directory)!=0;
                            policy.ValidateEntry(path,directory);
                            SafeFileHandle handle=MigrationNativeFiles.OpenStable(path,!directory); var item=new OpenEntry {Handle=handle}; open.Add(item);
                            MigrationIdentity before=MigrationNativeFiles.Identity(handle,path);
                            if(relative.Length==0 && (before.Attributes&FileAttributes.Directory)==0) throw new IOException("Migration source root must be an ordinary directory.");
                            MigrationNativeFiles.RequireOrdinary(before,directory); MigrationNativeFiles.RequireNoStreams(path,directory);
                            if(relative.Length==0) policy.ValidateProjectAncestors(src,token,deadline);
                            if(!before.VolumeGuid.Equals(actualSourceVolume.Guid,StringComparison.OrdinalIgnoreCase) || before.VolumeSerial!=actualSourceVolume.Serial) throw new IOException("Subtree crossed a volume boundary.");
                            string hash=null;
                            if(directory) {
                                MigrationPolicy.RequireNoProjectMarkers(path,token,deadline); dirs++;
                                item.Children=Children(path,maxEntries-manifest.Count-1-pending.Count,token,deadline);
                                for(int i=item.Children.Length-1;i>=0;i--) pending.Push(item.Children[i]);
                            } else {
                                bytes=checked(bytes+before.Length); if(bytes>maxBytes) throw new IOException("Migration byte budget exhausted."); files++;
                                hash=MigrationNativeFiles.Hash(handle,token,deadline);
                            }
                            if(!before.Same(MigrationNativeFiles.Identity(handle,path))) throw new IOException("Entry changed during manifest capture.");
                            item.Entry=new MigrationEntry(relative,before,hash); manifest.Add(item.Entry);
                        }
                        // Keep every stable handle until issuance, then reject additions to each directory.
                        foreach(OpenEntry item in open) {
                            CheckWork(token,deadline);
                            if(!item.Entry.Identity.Same(MigrationNativeFiles.Identity(item.Handle,item.Entry.Identity.FinalPath))) throw new IOException("Source identity changed during preflight.");
                            MigrationNativeFiles.RequireNoStreams(item.Entry.Identity.FinalPath,item.Entry.IsDirectory);
                            if(item.Entry.IsDirectory && !EqualPaths(item.Children,Children(item.Entry.Identity.FinalPath,maxEntries,token,deadline))) throw new IOException("Directory membership changed during preflight.");
                        }
                        MigrationNativeFiles.RequireAbsent(dst);
                        ulong overhead=checked((ulong)Math.Max(64L*1024*1024,bytes/20)+((ulong)manifest.Count*4096));
                        if(destinationVolume.Available<checked((ulong)bytes+overhead)) throw new IOException("Insufficient target free space including migration overhead.");
                        CheckWork(token,deadline);
                        string digest=Digest(src,dst,sourceVolume,destinationVolume,sourceParentIdentity,targetParentIdentity,manifest,simulatedProbe!=null);
                        var snapshot=new MigrationSnapshot(src,dst,sourceVolume,destinationVolume,sourceParentIdentity,targetParentIdentity,manifest,digest,simulatedProbe!=null);
                        MigrationPlan result=MigrationPlan.Summary(src,dst,bytes,files,dirs); issued.Add(result,snapshot); return result;
                    }
                }
            } catch(OperationCanceledException) {throw;}
            catch(MigrationPreflightException) {throw;}
            catch(Exception ex) {
                if(!(ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is ArgumentException || ex is OverflowException || ex is System.Security.SecurityException)) throw;
                throw new MigrationPreflightException("Rejected","Migration preflight rejected the entire request: "+ex.Message,ex);
            } finally {for(int i=open.Count-1;i>=0;i--) open[i].Handle.Dispose();}
        }
        internal MigrationSnapshot Inspect(MigrationPlan plan) {
            MigrationSnapshot snapshot; if(plan==null || !issued.TryGetValue(plan,out snapshot)) throw new IOException("Plan was not issued by this planner."); return snapshot;
        }
        internal MigrationSnapshot GetSnapshotForExecution(MigrationPlan plan) {
            MigrationSnapshot snapshot=Inspect(plan); if(snapshot.IsSimulation) throw new IOException("A simulated volume plan has no execution authority."); return snapshot;
        }
        static void CheckWork(CancellationToken token,DateTime deadline) {token.ThrowIfCancellationRequested(); if(DateTime.UtcNow>deadline) throw new IOException("Migration work budget exhausted.");}
        static string[] Children(string path,int remaining,CancellationToken token,DateTime deadline) {
            var paths=new List<string>(); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string child in Directory.EnumerateFileSystemEntries(path)) {
                CheckWork(token,deadline); string full=MigrationNativeFiles.Normalize(child);
                if(paths.Count>=remaining || !seen.Add(full)) throw new IOException("Directory entry budget or duplicate-case name rejected."); paths.Add(full);
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase); return paths.ToArray();
        }
        static bool EqualPaths(string[] a,string[] b) {if(a.Length!=b.Length) return false; for(int i=0;i<a.Length;i++) if(!a[i].Equals(b[i],StringComparison.Ordinal)) return false; return true;}
        // Shared with the bounded evidence codec; recomputing a digest never issues a plan capability.
        internal static string Digest(string source,string destination,MigrationVolume sourceVolume,MigrationVolume destinationVolume,MigrationIdentity sourceParent,MigrationIdentity destinationParent,IEnumerable<MigrationEntry> entries,bool simulation) {
            using(var stream=new MemoryStream()) {
                using(var writer=new BinaryWriter(stream,TextEncoding,true)) {
                    writer.Write(1); writer.Write(source); writer.Write(destination); writer.Write(simulation);
                    writer.Write(sourceVolume.Guid); writer.Write(sourceVolume.Serial); writer.Write(destinationVolume.Guid); writer.Write(destinationVolume.Serial);
                    WriteIdentity(writer,sourceParent); WriteIdentity(writer,destinationParent);
                    foreach(MigrationEntry e in entries) {
                        writer.Write(e.RelativePath); WriteIdentity(writer,e.Identity); writer.Write(e.Sha256??"");
                    }
                }
                stream.Position=0; using(var sha=SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(stream));
            }
        }
        static void WriteIdentity(BinaryWriter writer,MigrationIdentity identity) {
            writer.Write(identity.VolumeGuid); writer.Write(identity.VolumeSerial); writer.Write(identity.FileId); writer.Write(identity.FinalPath);
            writer.Write((int)identity.Attributes); writer.Write(identity.LastWriteFileTime); writer.Write(identity.Length); writer.Write(identity.Links);
        }
    }
}
