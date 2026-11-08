using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ClearDisk {
    // Durable evidence, never a deserializer of mutation capabilities. Recovery must independently revalidate native identities and policy.
    internal sealed class MigrationJournal {
        internal const int MaxRecordBytes=256*1024;
        const int Magic=0x31474D43;
        static readonly UTF8Encoding TextEncoding=new UTF8Encoding(false,true);
        static readonly byte[] Entropy=TextEncoding.GetBytes("ClearDisk.MigrationJournal.CurrentUser.v1.2026.10.independent");
        readonly string root,sid,digest,source,destination;
        readonly Guid transaction;
        readonly MigrationIdentity rootIdentity;
        internal const string StorageLeafName=".CDriveCleanerMigrationV1";
        // Profile storage has one identity across packaged/unpackaged launches; AppData may be MSIX-virtualized.
        internal static string CurrentUserStorage {get {return StorageForProfile(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));}}
        internal static string StorageForProfile(string profile) {
            string normalized=MigrationNativeFiles.Normalize(profile);
            if(normalized[0]!='C' || normalized.Length<=3) throw new IOException("Migration state requires the current user's non-root C: profile.");
            return Path.Combine(normalized,StorageLeafName);
        }
        internal MigrationJournal(MigrationPolicy policy,string directory,Guid transaction,string manifestDigest,string source,string destination) {
            if(policy==null || transaction==Guid.Empty) throw new IOException("Invalid journal context.");
            root=MigrationNativeFiles.Normalize(directory); this.source=MigrationNativeFiles.Normalize(source); this.destination=MigrationNativeFiles.Normalize(destination);
            if(!MigrationPolicy.Within(root,policy.ProfileRoot) || root.Equals(policy.ProfileRoot,StringComparison.OrdinalIgnoreCase)) throw new IOException("Journal must be private storage under the current profile.");
            if(MigrationPolicy.Within(root,this.source) || MigrationPolicy.Within(this.source,root) || MigrationPolicy.Within(root,this.destination) || MigrationPolicy.Within(this.destination,root)) throw new IOException("Journal storage must be disjoint from migration endpoints.");
            if(String.IsNullOrEmpty(manifestDigest)) throw new IOException("Missing manifest digest.");
            byte[] hash; try {hash=Convert.FromBase64String(manifestDigest);} catch(FormatException ex) {throw new IOException("Invalid manifest digest.",ex);}
            if(hash.Length!=32) throw new IOException("Invalid manifest digest length.");
            digest=manifestDigest; this.transaction=transaction; sid=WindowsIdentity.GetCurrent().User.Value;
            using(var parents=new MigrationNativeFiles.ParentLocks(root)) {
                if(!Directory.Exists(root)) MigrationNativeFiles.CreatePrivateDirectory(root);
                using(var handle=MigrationNativeFiles.OpenStable(root,false)) {
                    rootIdentity=MigrationNativeFiles.Identity(handle,root); MigrationNativeFiles.RequireOrdinary(rootIdentity,true);
                    MigrationNativeFiles.RequireNoStreams(root,true); MigrationNativeFiles.RequirePrivateDirectory(root);
                }
            }
        }
        string CheckpointPath(int sequence) {
            if(sequence<0 || sequence>(int)MigrationStage.CommittedBackupRetained) throw new IOException("Journal checkpoint count is bounded.");
            return Path.Combine(root,transaction.ToString("N")+"."+sequence.ToString("D3",System.Globalization.CultureInfo.InvariantCulture)+".checkpoint");
        }
        void RequireRoot() {
            using(var handle=MigrationNativeFiles.OpenStable(root,false)) {
                MigrationIdentity now=MigrationNativeFiles.Identity(handle,root);
                if(now.FileId!=rootIdentity.FileId || now.VolumeSerial!=rootIdentity.VolumeSerial || !now.VolumeGuid.Equals(rootIdentity.VolumeGuid,StringComparison.OrdinalIgnoreCase)) throw new IOException("Journal directory identity changed.");
                MigrationNativeFiles.RequireOrdinary(now,true); MigrationNativeFiles.RequireNoStreams(root,true); MigrationNativeFiles.RequirePrivateDirectory(root);
            }
        }
        internal string WriteCheckpoint(int sequence,MigrationStage stage) {
            string path=CheckpointPath(sequence); if((int)stage!=sequence) throw new IOException("Journal intent/observation stages must follow the fixed sequence.");
            using(var parents=new MigrationNativeFiles.ParentLocks(path)) {
                RequireRoot(); byte[] previous=new byte[0];
                if(sequence>0) LoadCheckpointCore(sequence-1,out previous);
                byte[] plain;
                using(var data=new MemoryStream()) {
                    using(var writer=new BinaryWriter(data,TextEncoding,true)) {
                        writer.Write(Magic); writer.Write(1); writer.Write(transaction.ToByteArray()); writer.Write(sequence); writer.Write((int)stage);
                        WriteText(writer,sid); WriteText(writer,digest); WriteText(writer,source); WriteText(writer,destination); writer.Write(previous.Length); writer.Write(previous);
                    }
                    plain=data.ToArray();
                }
                byte[] record=ProtectedData.Protect(plain,Entropy,DataProtectionScope.CurrentUser); if(record.Length>MaxRecordBytes) throw new IOException("Checkpoint exceeds maximum length.");
                // CreateNew cannot overwrite. WriteThrough plus Flush(true) persists the protected checkpoint before a future effect.
                using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) {
                    MigrationIdentity identity=MigrationNativeFiles.Identity(file.SafeFileHandle,path); MigrationNativeFiles.RequireOrdinary(identity,false);
                    file.Write(record,0,record.Length); file.Flush(true);
                }
                return path;
            }
        }
        internal MigrationCheckpoint LoadCheckpoint(int sequence) {
            byte[] authenticatedHash; return LoadCheckpointCore(sequence,out authenticatedHash);
        }
        MigrationCheckpoint LoadCheckpointCore(int sequence,out byte[] authenticatedHash) {
            authenticatedHash=new byte[0];
            string path=CheckpointPath(sequence);
            using(var parents=new MigrationNativeFiles.ParentLocks(path)) {
                RequireRoot(); MigrationCheckpoint result=null; byte[] previous=new byte[0];
                for(int index=0;index<=sequence;index++) {
                    byte[] record=ReadRecord(CheckpointPath(index));
                    try {
                        byte[] plain=ProtectedData.Unprotect(record,Entropy,DataProtectionScope.CurrentUser);
                        if(plain.Length>MaxRecordBytes) throw new IOException("Oversize decrypted checkpoint.");
                        using(var data=new MemoryStream(plain,false)) using(var reader=new BinaryReader(data,TextEncoding)) {
                            if(reader.ReadInt32()!=Magic || reader.ReadInt32()!=1) throw new IOException("Unknown checkpoint format.");
                            Guid tx=new Guid(ReadExact(reader,16)); int seq=reader.ReadInt32(),stage=reader.ReadInt32();
                            string recordedSid=ReadText(reader,256),recordedDigest=ReadText(reader,256),recordedSource=ReadText(reader,96000),recordedDestination=ReadText(reader,96000);
                            int priorLength=reader.ReadInt32(); if(priorLength!=previous.Length) throw new IOException("Broken checkpoint chain."); byte[] prior=ReadExact(reader,priorLength);
                            if(tx!=transaction || seq!=index || stage!=index || recordedSid!=sid || recordedDigest!=digest || recordedSource!=source || recordedDestination!=destination || !Equal(previous,prior) || data.Position!=data.Length) throw new IOException("Checkpoint context, sequence, stage or chain mismatch.");
                            result=new MigrationCheckpoint(tx,seq,(MigrationStage)stage,recordedDigest,recordedSource,recordedDestination);
                        }
                    } catch(CryptographicException ex) {throw new IOException("Checkpoint authentication failed.",ex);}
                    catch(ArgumentException ex) {throw new IOException("Invalid checkpoint encoding.",ex);}
                    previous=HashRecord(record);
                }
                // Return the hash of the same bytes whose protected payload was authenticated above.
                authenticatedHash=previous; return result;
            }
        }
        static byte[] ReadRecord(string path) {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                MigrationIdentity before=MigrationNativeFiles.Identity(file.SafeFileHandle,path); MigrationNativeFiles.RequireOrdinary(before,false); MigrationNativeFiles.RequireNoStreams(path,false);
                if(file.Length<=0 || file.Length>MaxRecordBytes) throw new IOException("Truncated or oversize checkpoint rejected.");
                byte[] result=new byte[(int)file.Length]; int offset=0;
                while(offset<result.Length) {int read=file.Read(result,offset,result.Length-offset); if(read==0) throw new IOException("Truncated checkpoint."); offset+=read;}
                if(!before.Same(MigrationNativeFiles.Identity(file.SafeFileHandle,path))) throw new IOException("Checkpoint identity changed while reading."); return result;
            }
        }
        static byte[] HashRecord(byte[] record) {using(var sha=SHA256.Create()) return sha.ComputeHash(record);}
        static bool Equal(byte[] a,byte[] b) {if(a.Length!=b.Length) return false; int diff=0; for(int i=0;i<a.Length;i++) diff|=a[i]^b[i]; return diff==0;}
        static void WriteText(BinaryWriter writer,string value) {
            byte[] bytes; try {bytes=TextEncoding.GetBytes(value);} catch(EncoderFallbackException ex) {throw new IOException("Malformed UTF-16 checkpoint text rejected without replacement.",ex);}
            if(bytes.Length>96000) throw new IOException("Checkpoint field too long."); writer.Write(bytes.Length); writer.Write(bytes);
        }
        static string ReadText(BinaryReader reader,int maximum) {int length=reader.ReadInt32(); if(length<0 || length>maximum) throw new IOException("Checkpoint field exceeds bounds."); return new UTF8Encoding(false,true).GetString(ReadExact(reader,length));}
        static byte[] ReadExact(BinaryReader reader,int length) {byte[] data=reader.ReadBytes(length); if(data.Length!=length) throw new IOException("Truncated checkpoint payload."); return data;}
    }
}
