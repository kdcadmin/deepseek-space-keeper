using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ClearDisk {
    // This mutable record is evidence only. It is never accepted as an execution/recovery capability.
    internal sealed class MigrationTransaction {
        internal readonly Guid Id;
        internal readonly MigrationSnapshot Source;
        internal int Revision=-1;
        internal MigrationIdentity StagingIdentity,JunctionIdentity;
        internal IList<MigrationEntry> TargetEntries;
        internal bool Committed,RestoreIntent,Restored,ReleaseIntent,ReleaseCompleted;
        internal MigrationTransaction(Guid id,MigrationSnapshot source) {Id=id; Source=source;}
        internal string Backup {get {return Path.Combine(Path.GetDirectoryName(Source.Source),".cdm-"+Id.ToString("N")+".backup");}}
        internal string Staging {get {return Path.Combine(Path.GetDirectoryName(Source.Destination),".cdm-"+Id.ToString("N")+".staging");}}
        internal string TemporaryJunction {get {return Path.Combine(Path.GetDirectoryName(Source.Source),".cdm-"+Id.ToString("N")+".junction");}}
    }
    internal sealed class MigrationManifestStore {
        internal const int MaximumBytes=32*1024*1024,MaximumRecords=32,MaximumEntries=50000;
        const int Magic=0x324D4443;
        static readonly UTF8Encoding TextEncoding=new UTF8Encoding(false,true);
        static readonly byte[] Entropy=TextEncoding.GetBytes("ClearDisk.FullMigrationManifest.CurrentUser.v2.2026.10");
        readonly MigrationPolicy policy;
        readonly string root,sid;
        readonly MigrationIdentity identity;
        readonly Action<string> authenticatedReadFixture;
        internal MigrationManifestStore(MigrationPolicy policy) : this(policy,MigrationJournal.CurrentUserStorage) {}
        // Evidence-only fixture seam. Production executor/recovery always use the fixed current-user root.
        internal MigrationManifestStore(MigrationPolicy policy,string evidenceDirectory) : this(policy,evidenceDirectory,null) {}
        internal MigrationManifestStore(MigrationPolicy policy,string evidenceDirectory,Action<string> authenticatedReadFixture) {
            if(policy==null) throw new ArgumentNullException("policy"); this.policy=policy;
            root=MigrationNativeFiles.Normalize(evidenceDirectory); sid=WindowsIdentity.GetCurrent().User.Value; this.authenticatedReadFixture=authenticatedReadFixture;
            if(!MigrationPolicy.Within(root,policy.ProfileRoot) || root==policy.ProfileRoot) throw new IOException("Manifest storage must be private current-profile storage.");
            using(var parents=new MigrationNativeFiles.ParentLocks(root)) {
                if(!Directory.Exists(root)) MigrationNativeFiles.CreatePrivateDirectory(root);
                using(var handle=MigrationNativeFiles.OpenStable(root,false)) {
                    identity=MigrationNativeFiles.Identity(handle,root); MigrationNativeFiles.RequireOrdinary(identity,true);
                    MigrationNativeFiles.RequireNoStreams(root,true); MigrationNativeFiles.RequirePrivateDirectory(root);
                }
            }
        }
        internal void Save(MigrationTransaction transaction) {
            Validate(transaction);
            if(MigrationPolicy.Within(root,transaction.Source.Source) || MigrationPolicy.Within(transaction.Source.Source,root) || MigrationPolicy.Within(root,transaction.Source.Destination) || MigrationPolicy.Within(transaction.Source.Destination,root)) throw new IOException("Manifest storage overlaps an endpoint.");
            int revision=checked(transaction.Revision+1); if(revision>=MaximumRecords) throw new IOException("Manifest revision budget exhausted.");
            using(var pins=new MigrationNativeFiles.ParentLocks(Path.Combine(root,".pin"))) {
                RequireRoot(); byte[] prior=new byte[0];
                if(revision>0) {
                    MigrationTransaction previous=LoadCore(transaction.Id,out prior);
                    if(previous.Revision!=transaction.Revision || previous.Source.ManifestDigest!=transaction.Source.ManifestDigest || previous.Committed&&!transaction.Committed || previous.RestoreIntent&&!transaction.RestoreIntent || previous.Restored&&!transaction.Restored || previous.ReleaseIntent&&!transaction.ReleaseIntent || previous.ReleaseCompleted&&!transaction.ReleaseCompleted) throw new IOException("Stale or regressing manifest evidence.");
                }
                byte[] record=Protect(transaction,revision,prior,sid);
                string path=FilePath(transaction.Id,revision);
                using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) {
                    MigrationNativeFiles.RequireOrdinary(MigrationNativeFiles.Identity(file.SafeFileHandle,path),false);
                    file.Write(record,0,record.Length); file.Flush(true);
                }
                transaction.Revision=revision;
            }
        }
        internal MigrationTransaction Load(Guid id) {
            byte[] authenticatedHash; return LoadCore(id,out authenticatedHash);
        }
        MigrationTransaction LoadCore(Guid id,out byte[] authenticatedHash) {
            authenticatedHash=new byte[0];
            if(id==Guid.Empty) throw new IOException("Missing transaction ID.");
            using(var pins=new MigrationNativeFiles.ParentLocks(Path.Combine(root,".pin"))) {
                RequireRoot(); var files=new SortedDictionary<int,string>(); int inspected=0;
                foreach(string path in Directory.EnumerateFiles(root)) {
                    if(++inspected>MaximumRecords*500) throw new IOException("Manifest discovery budget exhausted.");
                    string name=Path.GetFileName(path); if(!name.StartsWith(id.ToString("N")+".",StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".manifest",StringComparison.OrdinalIgnoreCase)) continue;
                    int revision; string part=name.Substring(33,name.Length-33-9);
                    if(part.Length!=3 || !Int32.TryParse(part,out revision) || revision<0 || revision>=MaximumRecords || name!=Path.GetFileName(FilePath(id,revision)) || files.ContainsKey(revision)) throw new IOException("Invalid manifest record filename.");
                    files.Add(revision,path);
                }
                if(files.Count==0) throw new IOException("No full manifest exists for this transaction.");
                byte[] prior=new byte[0]; MigrationTransaction result=null;
                for(int i=0;i<files.Count;i++) {
                    string path; if(!files.TryGetValue(i,out path)) throw new IOException("Manifest revision chain is incomplete.");
                    byte[] record=ReadRecord(path); MigrationTransaction next=Unprotect(record,id,i,prior,sid);
                    if(result!=null && (next.Source.ManifestDigest!=result.Source.ManifestDigest || result.Committed&&!next.Committed || result.RestoreIntent&&!next.RestoreIntent || result.Restored&&!next.Restored || result.ReleaseIntent&&!next.ReleaseIntent || result.ReleaseCompleted&&!next.ReleaseCompleted)) throw new IOException("Manifest context changed or state regressed.");
                    if(authenticatedReadFixture!=null) authenticatedReadFixture(path);
                    result=next; prior=Hash(record);
                }
                authenticatedHash=prior; return result;
            }
        }
        void RequireRoot() {
            using(var handle=MigrationNativeFiles.OpenStable(root,false)) {
                MigrationIdentity now=MigrationNativeFiles.Identity(handle,root);
                if(now.FileId!=identity.FileId || now.VolumeSerial!=identity.VolumeSerial || now.VolumeGuid!=identity.VolumeGuid) throw new IOException("Manifest directory identity changed.");
                MigrationNativeFiles.RequireOrdinary(now,true); MigrationNativeFiles.RequireNoStreams(root,true); MigrationNativeFiles.RequirePrivateDirectory(root);
            }
        }
        string FilePath(Guid id,int revision) {return Path.Combine(root,id.ToString("N")+"."+revision.ToString("D3",System.Globalization.CultureInfo.InvariantCulture)+".manifest");}
        static byte[] ReadRecord(string path) {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                MigrationIdentity before=MigrationNativeFiles.Identity(file.SafeFileHandle,path); MigrationNativeFiles.RequireOrdinary(before,false); MigrationNativeFiles.RequireNoStreams(path,false);
                if(file.Length<=0 || file.Length>MaximumBytes) throw new IOException("Manifest record exceeds bounds.");
                byte[] data=new byte[(int)file.Length]; int offset=0;
                while(offset<data.Length) {int n=file.Read(data,offset,data.Length-offset); if(n==0) throw new IOException("Truncated manifest record."); offset+=n;}
                if(!before.Same(MigrationNativeFiles.Identity(file.SafeFileHandle,path))) throw new IOException("Manifest record changed during reading."); return data;
            }
        }
        internal static byte[] Protect(MigrationTransaction tx,int revision,byte[] prior,string sid) {
            Validate(tx); if(revision<0 || revision>=MaximumRecords || prior.Length!=(revision==0?0:32)) throw new IOException("Invalid manifest chain context.");
            byte[] plain;
            using(var memory=new MemoryStream()) {
                using(var writer=new BinaryWriter(memory,TextEncoding,true)) {
                    writer.Write(Magic); writer.Write(2); Text(writer,sid); writer.Write(tx.Id.ToByteArray()); writer.Write(revision); writer.Write(prior.Length); writer.Write(prior);
                    MigrationSnapshot s=tx.Source; Text(writer,s.Source); Text(writer,s.Destination); Text(writer,s.ManifestDigest);
                    Volume(writer,s.SourceVolume); Volume(writer,s.DestinationVolume); Identity(writer,s.SourceParent); Identity(writer,s.DestinationParent); Entries(writer,s.Entries);
                    OptionalIdentity(writer,tx.StagingIdentity); OptionalIdentity(writer,tx.JunctionIdentity); writer.Write(tx.TargetEntries!=null); if(tx.TargetEntries!=null) Entries(writer,tx.TargetEntries);
                    writer.Write(tx.Committed); writer.Write(tx.RestoreIntent); writer.Write(tx.Restored); writer.Write(tx.ReleaseIntent); writer.Write(tx.ReleaseCompleted);
                }
                if(memory.Length>MaximumBytes-4096) throw new IOException("Full manifest exceeds the storage budget."); plain=memory.ToArray();
            }
            byte[] result=ProtectedData.Protect(plain,Entropy,DataProtectionScope.CurrentUser); if(result.Length>MaximumBytes) throw new IOException("Protected manifest exceeds the storage budget."); return result;
        }
        internal static MigrationTransaction Unprotect(byte[] record,Guid expectedId,int revision,byte[] prior,string expectedSid) {
            if(record==null || record.Length==0 || record.Length>MaximumBytes) throw new IOException("Invalid manifest record size.");
            try {
                byte[] plain=ProtectedData.Unprotect(record,Entropy,DataProtectionScope.CurrentUser); if(plain.Length>MaximumBytes) throw new IOException("Oversize decrypted manifest.");
                using(var memory=new MemoryStream(plain,false)) using(var reader=new BinaryReader(memory,TextEncoding)) {
                    if(reader.ReadInt32()!=Magic || reader.ReadInt32()!=2 || Text(reader,256)!=expectedSid) throw new IOException("Manifest version or SID mismatch.");
                    Guid id=new Guid(Exact(reader,16)); int sequence=reader.ReadInt32(),priorLength=reader.ReadInt32();
                    if(id!=expectedId || id==Guid.Empty || sequence!=revision || revision<0 || revision>=MaximumRecords || priorLength!=prior.Length || priorLength!=(revision==0?0:32) || !Equal(Exact(reader,priorLength),prior)) throw new IOException("Manifest transaction or chain mismatch.");
                    string source=Text(reader,96000),destination=Text(reader,96000),digest=Text(reader,256);
                    MigrationVolume sourceVolume=Volume(reader),destinationVolume=Volume(reader); MigrationIdentity sourceParent=Identity(reader),destinationParent=Identity(reader); IList<MigrationEntry> entries=Entries(reader);
                    var snapshot=new MigrationSnapshot(source,destination,sourceVolume,destinationVolume,sourceParent,destinationParent,entries,digest,false);
                    var tx=new MigrationTransaction(id,snapshot); tx.Revision=revision; tx.StagingIdentity=OptionalIdentity(reader); tx.JunctionIdentity=OptionalIdentity(reader); if(reader.ReadBoolean()) tx.TargetEntries=Entries(reader);
                    tx.Committed=reader.ReadBoolean(); tx.RestoreIntent=reader.ReadBoolean(); tx.Restored=reader.ReadBoolean(); tx.ReleaseIntent=reader.ReadBoolean(); tx.ReleaseCompleted=reader.ReadBoolean();
                    if(memory.Position!=memory.Length) throw new IOException("Trailing manifest data."); Validate(tx); return tx;
                }
            } catch(CryptographicException ex) {throw new IOException("Full manifest authentication failed.",ex);}
            catch(ArgumentException ex) {throw new IOException("Malformed full manifest.",ex);}
            catch(OverflowException ex) {throw new IOException("Manifest overflow.",ex);}
        }
        internal static void Validate(MigrationTransaction tx) {
            if(tx==null || tx.Id==Guid.Empty || tx.Source==null || tx.Source.IsSimulation) throw new IOException("Invalid transaction evidence.");
            MigrationSnapshot s=tx.Source; if(MigrationNativeFiles.Normalize(s.Source)!=s.Source || MigrationNativeFiles.Normalize(s.Destination)!=s.Destination || MigrationPolicy.Within(s.Source,s.Destination) || MigrationPolicy.Within(s.Destination,s.Source)) throw new IOException("Invalid manifest endpoints.");
            CheckVolume(s.SourceVolume); CheckVolume(s.DestinationVolume); CheckIdentity(s.SourceParent,Path.GetDirectoryName(s.Source),s.SourceVolume); CheckIdentity(s.DestinationParent,Path.GetDirectoryName(s.Destination),s.DestinationVolume);
            CheckEntries(s.Entries,s.Source,s.SourceVolume);
            if(s.ManifestDigest!=MigrationPlanner.Digest(s.Source,s.Destination,s.SourceVolume,s.DestinationVolume,s.SourceParent,s.DestinationParent,s.Entries,false)) throw new IOException("Manifest digest mismatch.");
            if(tx.StagingIdentity!=null) {CheckIdentity(tx.StagingIdentity,tx.Staging,s.DestinationVolume); MigrationNativeFiles.RequireOrdinary(tx.StagingIdentity,true);}
            if(tx.TargetEntries!=null) {
                CheckEntries(tx.TargetEntries,tx.Staging,s.DestinationVolume); if(tx.StagingIdentity==null || !SameObject(tx.StagingIdentity,tx.TargetEntries[0].Identity) || tx.TargetEntries.Count!=s.Entries.Count) throw new IOException("Target root/count binding mismatch.");
                for(int i=0;i<s.Entries.Count;i++) {MigrationEntry a=s.Entries[i],b=tx.TargetEntries[i]; if(a.RelativePath!=b.RelativePath || a.IsDirectory!=b.IsDirectory || a.Sha256!=b.Sha256 || a.Identity.Attributes!=b.Identity.Attributes || a.Identity.LastWriteFileTime!=b.Identity.LastWriteFileTime || !a.IsDirectory&&a.Identity.Length!=b.Identity.Length) throw new IOException("Target baseline differs from the copied source manifest.");}
            }
            if(tx.JunctionIdentity!=null) {CheckIdentity(tx.JunctionIdentity,tx.TemporaryJunction,s.SourceVolume); if((tx.JunctionIdentity.Attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=(FileAttributes.Directory|FileAttributes.ReparsePoint)) throw new IOException("Invalid recorded junction identity.");}
            if(tx.Committed && (tx.TargetEntries==null || tx.JunctionIdentity==null) || tx.Restored&&!tx.RestoreIntent || tx.ReleaseCompleted&&!tx.ReleaseIntent || tx.ReleaseIntent&&(!tx.Committed || tx.RestoreIntent)) throw new IOException("Invalid transaction state.");
        }
        static void CheckEntries(IList<MigrationEntry> entries,string root,MigrationVolume volume) {
            if(entries==null || entries.Count==0 || entries.Count>MaximumEntries || entries[0].RelativePath!="" || !entries[0].IsDirectory) throw new IOException("Invalid manifest entry count/root.");
            var parents=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(MigrationEntry e in entries) {
                if(e==null || e.Identity==null || e.RelativePath==null) throw new IOException("Missing manifest entry.");
                string path=e.RelativePath==""?root:Path.Combine(root,e.RelativePath);
                if(e.RelativePath!="" && (Path.IsPathRooted(e.RelativePath) || !parents.Contains(Path.GetDirectoryName(e.RelativePath)??""))) throw new IOException("Unnormalized entry or missing directory parent.");
                if(e.RelativePath!="") foreach(string part in e.RelativePath.Split('\\')) MigrationNativeFiles.ValidateComponent(part);
                if(!seen.Add(e.RelativePath)) throw new IOException("Duplicate manifest member."); CheckIdentity(e.Identity,path,volume); MigrationNativeFiles.RequireOrdinary(e.Identity,e.IsDirectory);
                if(e.IsDirectory) {if(e.Sha256!=null) throw new IOException("Directory data hash rejected."); parents.Add(e.RelativePath);} else {byte[] hash=Convert.FromBase64String(e.Sha256??""); if(hash.Length!=32) throw new IOException("Invalid file hash.");}
            }
        }
        static void CheckIdentity(MigrationIdentity identity,string expectedPath,MigrationVolume volume) {
            if(identity==null || identity.FileId==0 || identity.VolumeSerial!=volume.Serial || identity.VolumeGuid!=volume.Guid || identity.FinalPath!=expectedPath || MigrationNativeFiles.Normalize(identity.FinalPath)!=identity.FinalPath || identity.Length<0 || identity.Links==0 || identity.LastWriteFileTime<0) throw new IOException("Invalid full native identity.");
        }
        static void CheckVolume(MigrationVolume volume) {
            if(volume==null || volume.Guid==null || !volume.Guid.StartsWith("\\\\?\\Volume{",StringComparison.Ordinal) || !volume.Guid.EndsWith("}\\",StringComparison.Ordinal) || volume.Guid.Length!=49) throw new IOException("Invalid volume GUID.");
            Guid parsed; if(!Guid.TryParse(volume.Guid.Substring(11,36),out parsed) || parsed==Guid.Empty) throw new IOException("Invalid volume GUID encoding.");
        }
        internal static bool SameObject(MigrationIdentity a,MigrationIdentity b) {return a!=null && b!=null && a.FileId==b.FileId && a.VolumeSerial==b.VolumeSerial && a.VolumeGuid.Equals(b.VolumeGuid,StringComparison.OrdinalIgnoreCase);}
        internal static string StateKey(MigrationTransaction tx) {
            Validate(tx);
            using(var memory=new MemoryStream()) {
                using(var writer=new BinaryWriter(memory,TextEncoding,true)) {
                    writer.Write(tx.Id.ToByteArray()); writer.Write(tx.Revision); Text(writer,tx.Source.ManifestDigest); OptionalIdentity(writer,tx.StagingIdentity); OptionalIdentity(writer,tx.JunctionIdentity);
                    writer.Write(tx.TargetEntries!=null); if(tx.TargetEntries!=null) Entries(writer,tx.TargetEntries);
                    writer.Write(tx.Committed); writer.Write(tx.RestoreIntent); writer.Write(tx.Restored); writer.Write(tx.ReleaseIntent); writer.Write(tx.ReleaseCompleted);
                }
                return Convert.ToBase64String(Hash(memory.ToArray()));
            }
        }
        static void Entries(BinaryWriter w,IList<MigrationEntry> entries) {w.Write(entries.Count); foreach(MigrationEntry e in entries) {Text(w,e.RelativePath); Identity(w,e.Identity); Text(w,e.Sha256??"");}}
        static IList<MigrationEntry> Entries(BinaryReader r) {int n=r.ReadInt32(); if(n<1 || n>MaximumEntries) throw new IOException("Entry record limit exceeded."); var result=new List<MigrationEntry>(n); for(int i=0;i<n;i++) {string relative=Text(r,96000); MigrationIdentity id=Identity(r); string hash=Text(r,256); result.Add(new MigrationEntry(relative,id,hash==""?null:hash));} return result;}
        static void Volume(BinaryWriter w,MigrationVolume v) {Text(w,v.Guid); w.Write(v.Serial); w.Write(v.Available);}
        static MigrationVolume Volume(BinaryReader r) {return new MigrationVolume(Text(r,256),r.ReadUInt32(),r.ReadUInt64());}
        static void OptionalIdentity(BinaryWriter w,MigrationIdentity id) {w.Write(id!=null); if(id!=null) Identity(w,id);}
        static MigrationIdentity OptionalIdentity(BinaryReader r) {return r.ReadBoolean()?Identity(r):null;}
        static void Identity(BinaryWriter w,MigrationIdentity id) {Text(w,id.VolumeGuid); w.Write(id.VolumeSerial); w.Write(id.FileId); Text(w,id.FinalPath); w.Write((int)id.Attributes); w.Write(id.LastWriteFileTime); w.Write(id.Length); w.Write(id.Links);}
        static MigrationIdentity Identity(BinaryReader r) {return new MigrationIdentity(Text(r,256),r.ReadUInt32(),r.ReadUInt64(),Text(r,96000),(FileAttributes)r.ReadInt32(),r.ReadInt64(),r.ReadInt64(),r.ReadUInt32());}
        static void Text(BinaryWriter w,string value) {
            byte[] bytes; try {bytes=TextEncoding.GetBytes(value);} catch(EncoderFallbackException ex) {throw new IOException("Malformed UTF-16 manifest text rejected without replacement.",ex);}
            if(bytes.Length>96000 || w.BaseStream.Position+bytes.Length+4>MaximumBytes-4096) throw new IOException("Manifest text or total record exceeds bounds."); w.Write(bytes.Length); w.Write(bytes);
        }
        static string Text(BinaryReader r,int maximum) {int n=r.ReadInt32(); if(n<0 || n>maximum) throw new IOException("Manifest field exceeds bounds."); return new UTF8Encoding(false,true).GetString(Exact(r,n));}
        static byte[] Exact(BinaryReader r,int count) {byte[] data=r.ReadBytes(count); if(data.Length!=count) throw new IOException("Truncated manifest payload."); return data;}
        static byte[] Hash(byte[] data) {using(var sha=SHA256.Create()) return sha.ComputeHash(data);}
        static bool Equal(byte[] a,byte[] b) {if(a.Length!=b.Length) return false; int diff=0; for(int i=0;i<a.Length;i++) diff|=a[i]^b[i]; return diff==0;}
    }
}
