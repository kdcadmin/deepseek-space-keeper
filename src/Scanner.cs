using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ClearDisk {
    public sealed class Scanner {
        readonly SafetyPolicy policy;
        const int FileLimit = 2000, CacheLimit = 200000;
        public Scanner(SafetyPolicy safety) { if (safety == null) throw new ArgumentNullException("safety"); policy = safety; }
        public ScanReport ScanCaches(IEnumerable<CacheRule> rules, CancellationToken token, Action<ScanProgress> progress) {
            var report = new ScanReport { Mode = "cache" };
            var clock = Stopwatch.StartNew(); var pulse = new ScanPulse(progress);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try {
                foreach (CacheRule rule in rules) {
                    token.ThrowIfCancellationRequested();
                    if (rule == null || String.IsNullOrWhiteSpace(rule.Root)) continue;
                    string root = Path.GetFullPath(rule.Root).TrimEnd('\\');
                    if (!roots.Add(root)) continue;
                    if (!Directory.Exists(root)) continue;
                    bool trusted = rule.Risk == RiskLevel.Safe && policy.AuthorizeRule(rule);
                    var reviewUsage = new FolderUsage { Path = root };
                    bool scanned=false;
                    try {
                    scanned = ScanWalker.Walk(policy, root, false, report, token, delegate(string path, FileInfo info) {
                        if (!seen.Add(path)) return;
                        report.ScannedFiles++; report.LogicalBytes += info.Length;
                        reviewUsage.Files++; reviewUsage.Bytes+=info.Length;
                        pulse.Send(path, report);
                        if (rule.Risk != RiskLevel.Safe || !trusted) return;
                        bool eligible = trusted && Rules.Matches(rule, path, info.LastWriteTimeUtc) && !policy.IsPersonalOrRuntime(path);
                        if (!eligible && rule.Risk == RiskLevel.Safe) { report.Skipped++; return; }
                        if (report.Files.Count >= CacheLimit) { ScanWalker.Warn(report, "缓存明细已达到 200000 条上限，总量统计继续；每批清理后请重新扫描。",true); return; }
                        var file = new FileRecord { Path = path, Root = root, RuleId = rule.Id, Category = rule.Name,
                            Reason = rule.Reason, Risk = rule.Risk, Length = info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks };
                        if (eligible) {
                            try {
                                file.Stamp = policy.Capture(path, root);
                                file.Length = file.Stamp.Length; file.LastWriteTicks = file.Stamp.LastWriteTicks;
                                file.CanDelete = file.Stamp.Links == 1 && Rules.Matches(rule, path, new DateTime(file.LastWriteTicks, DateTimeKind.Utc));
                                if (file.Stamp.Links != 1) { file.Risk = RiskLevel.Protected; file.Reason = "硬链接文件，禁止自动清理。"; }
                            } catch (Exception e) { if (!ScanWalker.IsFileError(e)) throw; file.Risk = RiskLevel.Protected; file.Reason = "文件身份核验失败：" + e.Message; report.Skipped++; }
                        }
                        report.Files.Add(file);
                    }, rule.Risk == RiskLevel.Review);
                    } finally {
                        if (scanned || reviewUsage.Files>0) report.Folders.Add(reviewUsage);
                    }
                }
            } catch (OperationCanceledException) { report.Cancelled = true; }
            Finish(report, clock); return report;
        }
        public ScanReport ScanDisk(string root, CancellationToken token, Action<ScanProgress> progress) {
            var report = new ScanReport { Mode = "disk", Root = root };
            var clock = Stopwatch.StartNew(); var pulse = new ScanPulse(progress);
            var folders = new Dictionary<string, FolderUsage>(StringComparer.OrdinalIgnoreCase);
            var largest = new SortedSet<FileRecord>(new LargestComparer());
            try {
                ScanWalker.Walk(policy, root, true, report, token, delegate(string path, FileInfo info) {
                    report.ScannedFiles++; report.LogicalBytes += info.Length;
                    string parent = info.DirectoryName;
                    while (parent != null && SafetyPolicy.IsWithin(parent, root)) {
                        FolderUsage usage;
                        if (!folders.TryGetValue(parent, out usage)) { usage = new FolderUsage { Path = parent }; folders.Add(parent, usage); }
                        usage.Files++; usage.Bytes += info.Length;
                        if (String.Equals(parent.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) break;
                        parent = Path.GetDirectoryName(parent);
                    }
                    largest.Add(new FileRecord { Path = path, Root = root, Category = "大文件（仅查看）", Risk = RiskLevel.Review,
                        Length = info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks, Reason = "请在资源管理器中查看，本版本不自动删除个人文件。" });
                    if (largest.Count > FileLimit) largest.Remove(largest.Min);
                    pulse.Send(path, report);
                });
            } catch (OperationCanceledException) { report.Cancelled = true; }
            report.Files.AddRange(largest.Reverse());
            // Only the retained details require native identity capture, never every statistics file.
            foreach (FileRecord file in report.Files) {
                if (token.IsCancellationRequested) { report.Cancelled = true; break; }
                try { file.Stamp = policy.Capture(file.Path, root); file.CanOrganize=policy.CanOrganize(file); if (file.Stamp.Links != 1) { file.Risk = RiskLevel.Protected; file.Reason = "硬链接文件，仅供查看。"; } else if(file.CanOrganize) file.Reason="个人文件，默认不选；可逐项确认后移入可恢复隔离区。"; }
                catch (Exception e) { if (!ScanWalker.IsFileError(e)) throw; file.Reason = "文件已变化或无法核验身份，仅供查看。"; }
            }
            report.Folders.AddRange(folders.Values.OrderByDescending(f => f.Bytes).Take(2000));
            if (folders.Count > 2000) ScanWalker.Warn(report, "文件夹明细仅显示最大的 2000 个目录；父目录总量包含全部已遍历文件。",true);
            if (report.ScannedFiles > FileLimit) ScanWalker.Warn(report, "文件明细仅显示最大的 2000 个文件；计数包含全部已遍历的普通文件。",true);
            Finish(report, clock); return report;
        }
        static void Finish(ScanReport report, Stopwatch clock) {
            report.Seconds = clock.Elapsed.TotalSeconds;
            if (report.Cancelled) ScanWalker.Warn(report, "扫描已取消，结果与统计仅包含已完成部分。",true);
            if (report.Skipped > 0) ScanWalker.Warn(report, "已跳过 " + report.Skipped + " 个受保护、无权限、链接、离线或不符合规则的项目；目录大小仅为已遍历文件的逻辑字节数。",true);
        }
        sealed class LargestComparer : IComparer<FileRecord> {
            public int Compare(FileRecord a, FileRecord b) { int n = a.Length.CompareTo(b.Length); return n != 0 ? n : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path); }
        }
    }
    internal sealed class ScanPulse {
        readonly Action<ScanProgress> progress; readonly Stopwatch clock = Stopwatch.StartNew(); long last = -200;
        internal ScanPulse(Action<ScanProgress> callback) { progress = callback; }
        internal void Send(string message, ScanReport report) {
            if (progress != null && clock.ElapsedMilliseconds - last >= 200) { last = clock.ElapsedMilliseconds; progress(new ScanProgress { Message = message, Files = report.ScannedFiles, Bytes = report.LogicalBytes }); }
        }
    }
    internal static class ScanWalker {
        internal static bool IsFileError(Exception e) { return e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception || e is System.Security.SecurityException || e is ArgumentException || e is NotSupportedException; }
        internal static void Warn(ScanReport report, string message,bool summary=false) {
            if(report.Warnings.Contains(message)) return;
            // Reserve summary capacity after many protected or inaccessible directories.
            if(report.Warnings.Count<(summary ? 100 : 90)) report.Warnings.Add(message);
        }
        internal static bool Walk(SafetyPolicy policy, string root, bool driveRoot, ScanReport report, CancellationToken token, Action<string, FileInfo> visit, bool inspection = false) {
            token.ThrowIfCancellationRequested();
            string full;
            try {
                full = Path.GetFullPath(root);
                bool isDrive = driveRoot && full.Equals("C:\\", StringComparison.OrdinalIgnoreCase);
                FileAttributes blocked = FileAttributes.ReparsePoint | FileAttributes.Offline | (isDrive ? 0 : FileAttributes.System);
                if ((!isDrive && !(inspection ? policy.ValidateInspectionRoot(full) : policy.ValidateRoot(full))) || (File.GetAttributes(full) & blocked) != 0) {
                    report.Skipped++; Warn(report, "已跳过受保护或链接扫描根目录，占用未知：" + root); return false;
                }
            } catch (Exception e) { if (!IsFileError(e)) throw; report.Skipped++; Warn(report, "无法访问扫描根目录，占用未知：" + root); return false; }
            var pending = new Stack<string>(); pending.Push(full);
            while (pending.Count != 0) {
                token.ThrowIfCancellationRequested(); string dir = pending.Pop();
                try {
                    using (var pins = new NativeFiles.ParentLocks(Path.Combine(dir, ".walk"))) {
                    FileAttributes blocked = FileAttributes.ReparsePoint | FileAttributes.Offline | (dir.Equals("C:\\", StringComparison.OrdinalIgnoreCase) ? 0 : FileAttributes.System);
                    if ((File.GetAttributes(dir) & blocked) != 0) { report.Skipped++; continue; }
                    foreach (string path in Directory.EnumerateFileSystemEntries(dir)) {
                        token.ThrowIfCancellationRequested();
                        try {
                            FileAttributes attrs = File.GetAttributes(path);
                            bool directory = (attrs & FileAttributes.Directory) != 0;
                            if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline)) != 0 || policy.IsProtectedScanEntry(path,directory,inspection)) {
                                report.Skipped++;
                                if (directory && policy.IsPersonalOrRuntime(path)) Warn(report, "已排除运行环境或项目目录，占用未知：" + path);
                                continue;
                            }
                            if (directory) pending.Push(path);
                            else visit(path, new FileInfo(path));
                        } catch (Exception e) { if (!IsFileError(e)) throw; report.Skipped++; }
                    }
                    }
                } catch (Exception e) { if (!IsFileError(e)) throw; report.Skipped++; Warn(report, "部分项目无法访问：" + dir); }
            }
            return true;
        }
    }
}
