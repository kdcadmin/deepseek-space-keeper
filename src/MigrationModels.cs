using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace ClearDisk {
    internal sealed class MigrationVolume {
        internal readonly string Guid;
        internal readonly uint Serial;
        internal readonly ulong Available;
        internal MigrationVolume(string guid,uint serial,ulong available) {Guid=guid; Serial=serial; Available=available;}
    }
    internal sealed class MigrationIdentity {
        internal readonly string VolumeGuid,FinalPath;
        internal readonly uint VolumeSerial,Links;
        internal readonly ulong FileId;
        internal readonly FileAttributes Attributes;
        internal readonly long LastWriteFileTime,Length;
        internal MigrationIdentity(string volumeGuid,uint serial,ulong fileId,string path,FileAttributes attributes,long written,long length,uint links) {
            VolumeGuid=volumeGuid; VolumeSerial=serial; FileId=fileId; FinalPath=path; Attributes=attributes; LastWriteFileTime=written; Length=length; Links=links;
        }
        internal bool Same(MigrationIdentity other) {
            return other!=null && VolumeSerial==other.VolumeSerial && FileId==other.FileId && Attributes==other.Attributes && Length==other.Length && LastWriteFileTime==other.LastWriteFileTime && Links==other.Links &&
                VolumeGuid.Equals(other.VolumeGuid,StringComparison.OrdinalIgnoreCase) && FinalPath.Equals(other.FinalPath,StringComparison.OrdinalIgnoreCase);
        }
    }
    internal sealed class MigrationEntry {
        internal readonly string RelativePath,Sha256;
        internal readonly MigrationIdentity Identity;
        internal bool IsDirectory {get {return (Identity.Attributes&FileAttributes.Directory)!=0;}}
        internal MigrationEntry(string relative,MigrationIdentity identity,string sha256) {RelativePath=relative; Identity=identity; Sha256=sha256;}
    }
    internal sealed class MigrationSnapshot {
        internal readonly string Source,Destination,ManifestDigest;
        internal readonly MigrationVolume SourceVolume,DestinationVolume;
        internal readonly MigrationIdentity SourceParent,DestinationParent;
        internal readonly ReadOnlyCollection<MigrationEntry> Entries;
        internal readonly bool IsSimulation;
        internal MigrationSnapshot(string source,string destination,MigrationVolume sourceVolume,MigrationVolume destinationVolume,MigrationIdentity sourceParent,MigrationIdentity destinationParent,IList<MigrationEntry> entries,string digest,bool simulation) {
            Source=source; Destination=destination; SourceVolume=sourceVolume; DestinationVolume=destinationVolume;
            SourceParent=sourceParent; DestinationParent=destinationParent;
            // Entries and identity values are sealed and readonly; the collection owns a new array.
            Entries=new ReadOnlyCollection<MigrationEntry>(new List<MigrationEntry>(entries).ToArray()); ManifestDigest=digest; IsSimulation=simulation;
        }
    }
    // A summary is not executable authority. Only its issuing planner's weak registry recognizes it.
    internal sealed class MigrationPlan {
        readonly string source,destination;
        readonly long bytes;
        readonly int files,directories;
        private MigrationPlan(string source,string destination,long bytes,int files,int directories) {this.source=source; this.destination=destination; this.bytes=bytes; this.files=files; this.directories=directories;}
        internal static MigrationPlan Summary(string source,string destination,long bytes,int files,int directories) {return new MigrationPlan(source,destination,bytes,files,directories);}
        public string Source {get{return source;}}
        public string Destination {get{return destination;}}
        public long Bytes {get{return bytes;}}
        public int FileCount {get{return files;}}
        public int DirectoryCount {get{return directories;}}
    }
    internal sealed class MigrationPreflightException : IOException {
        internal readonly string Code;
        internal MigrationPreflightException(string code,string message,Exception inner) : base(message,inner) {Code=code;}
    }
    internal enum MigrationStage {
        Prepared=0,SourceRenameIntent=1,SourceBackedUp=2,CopyIntent=3,CopyVerified=4,PublishIntent=5,TargetPublished=6,JunctionIntent=7,CommittedBackupRetained=8
    }
    internal sealed class MigrationCheckpoint {
        internal readonly Guid Transaction;
        internal readonly int Sequence;
        internal readonly MigrationStage Stage;
        internal readonly string ManifestDigest,Source,Destination;
        internal MigrationCheckpoint(Guid transaction,int sequence,MigrationStage stage,string digest,string source,string destination) {Transaction=transaction; Sequence=sequence; Stage=stage; ManifestDigest=digest; Source=source; Destination=destination;}
        // Loaded records are evidence only. There is deliberately no conversion to MigrationPlan.
    }
}
