using System;
using System.Collections.Generic;

namespace ClearDisk {
    public enum RiskLevel { Safe, Review, Protected }
    public sealed class FileStamp {
        public uint Volume;
        public ulong FileId;
        public long Length;
        public long LastWriteTicks;
        public uint Links;
        public string FinalPath;
    }
    public sealed class CacheRule {
        public string Id, Name, Root, Reason;
        public RiskLevel Risk;
        public int MinAgeDays;
        public string[] Extensions;
    }
    public sealed class FileRecord {
        public string Path, Root, RuleId, Category, Reason, DuplicateGroup;
        public RiskLevel Risk;
        public long Length, LastWriteTicks;
        public FileStamp Stamp;
        public bool CanDelete, CanRecycle, CanOrganize, IsKeeper;
        internal FileRecord KeeperRecord {get;set;}
        internal string ContentHash {get;set;}
    }
    public sealed class FolderUsage {
        public string Path;
        public long Bytes, Files;
    }
    public sealed class ScanProgress {
        public string Message;
        public long Files, Bytes;
    }
    public sealed class ScanReport {
        public string Mode, Root;
        public List<FileRecord> Files = new List<FileRecord>();
        public List<FolderUsage> Folders = new List<FolderUsage>();
        public List<string> Warnings = new List<string>();
        public long ScannedFiles, LogicalBytes, Skipped;
        public bool Cancelled, Incomplete;
        public double Seconds;
        public DateTime StartedUtc = DateTime.UtcNow;
    }
    public sealed class CleanResult {
        public string Path, Action, Status, Reason;
        public long Bytes;
    }
    public sealed class CleanReport {
        public DateTime StartedUtc = DateTime.UtcNow;
        public long FreeBefore, FreeAfter;
        public bool Cancelled;
        public List<CleanResult> Results = new List<CleanResult>();
    }
    public static class Display {
        public static string Size(long bytes) {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double n = bytes; int u = 0;
            while (Math.Abs(n) >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
            return n.ToString(u == 0 ? "0" : "0.##") + " " + units[u];
        }
        public static string Risk(RiskLevel level) {
            return level == RiskLevel.Safe ? "可重建缓存" : level == RiskLevel.Review ? "需人工检查" : "受保护";
        }
    }
}
