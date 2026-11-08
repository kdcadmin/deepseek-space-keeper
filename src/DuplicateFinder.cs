using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace ClearDisk {
    public sealed class DuplicateFinder {
        readonly SafetyPolicy policy;
        const int CandidateLimit = 200000;
        public DuplicateFinder(SafetyPolicy safety) { if (safety == null) throw new ArgumentNullException("safety"); policy = safety; }
        public ScanReport Scan(string root, CancellationToken token, Action<ScanProgress> progress) {
            var report = new ScanReport { Mode = "duplicates", Root = root };
            var timer = Stopwatch.StartNew(); var pulse = new ScanPulse(progress);
            var candidates = new Dictionary<long, List<FileRecord>>(); int kept = 0;
            try {
                token.ThrowIfCancellationRequested();
                if (!PersonalDirectory(root)) { ScanWalker.Warn(report, "请选择普通个人文件目录，不可选择 AppData、软件、源代码项目、运行环境或模型仓库。"); return report; }
                ScanWalker.Walk(policy, root, false, report, token, delegate(string path, FileInfo info) {
                    report.ScannedFiles++; report.LogicalBytes += info.Length; pulse.Send("正在统计大小：" + path, report);
                    if (info.Length == 0 || !PersonalDirectory(info.DirectoryName) || SoftwareFile(path)) { report.Skipped++; return; }
                    if (kept >= CandidateLimit) { ScanWalker.Warn(report, "重复文件比对最多保留 200000 个候选文件；总量统计继续，分组可能不完整。",true); return; }
                    List<FileRecord> group;
                    if (!candidates.TryGetValue(info.Length, out group)) { group = new List<FileRecord>(); candidates.Add(info.Length, group); }
                    group.Add(new FileRecord { Path = path, Root = root, Length = info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                        Risk = RiskLevel.Review, Category = "重复个人文件", Reason = "内容完全一致，请在资源管理器中检查。本版本不自动删除个人文件。" }); kept++;
                });
                int groupNumber = 0;
                foreach (List<FileRecord> sizeGroup in candidates.Values) {
                    token.ThrowIfCancellationRequested(); if (sizeGroup.Count < 2) continue;
                    var partial = new Dictionary<string, List<FileRecord>>(StringComparer.Ordinal);
                    foreach (FileRecord file in sizeGroup) {
                        string hash = Hash(file, true, token, report);
                        if (hash != null) Add(partial, hash, file);
                        pulse.Send("正在比对文件样本：" + file.Path, report);
                    }
                    foreach (List<FileRecord> sampleGroup in partial.Values) {
                        if (sampleGroup.Count < 2) continue;
                        var full = new Dictionary<string, List<FileRecord>>(StringComparer.Ordinal);
                        foreach (FileRecord file in sampleGroup) {
                            string hash = Hash(file, false, token, report);
                            if (hash != null) Add(full, hash, file);
                            pulse.Send("正在比对 SHA-256：" + file.Path, report);
                        }
                        foreach (List<FileRecord> matches in full.Values) {
                            if (matches.Count < 2) continue;
                            // A deterministic keeper is locked even when all content hashes match.
                            matches.Sort(delegate(FileRecord a, FileRecord b) { return StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path); });
                            string id = "duplicate-" + (++groupNumber);
                            for (int i = 0; i < matches.Count; i++) { matches[i].DuplicateGroup = id; matches[i].IsKeeper = i == 0; matches[i].KeeperRecord=i==0?null:matches[0];matches[i].CanOrganize=policy.CanOrganize(matches[i]);matches[i].Reason=i==0?"此组保留项，不能选择整理。":"完整 SHA-256 相同；默认不选，可手动隔离。执行时核验并锁定保留项。"; report.Files.Add(matches[i]); }
                        }
                    }
                }
            } catch (OperationCanceledException) { report.Cancelled = true; ScanWalker.Warn(report, "重复文件扫描已取消，当前结果不完整。",true); }
            finally { report.Seconds = timer.Elapsed.TotalSeconds; }
            if (report.Skipped > 0) ScanWalker.Warn(report, "已跳过 " + report.Skipped + " 个受保护、链接、空白、已变化或无法访问的项目。",true);
            return report;
        }
        static void Add(Dictionary<string, List<FileRecord>> table, string hash, FileRecord file) {
            List<FileRecord> list; if (!table.TryGetValue(hash, out list)) { list = new List<FileRecord>(); table.Add(hash, list); } list.Add(file);
        }
        bool PersonalDirectory(string path) {
            if (String.IsNullOrWhiteSpace(path) || !SafetyPolicy.IsWithin(path, policy.ProfileRoot) || policy.IsPersonalOrRuntime(path)) return false;
            string full = Path.GetFullPath(path);
            foreach (string segment in full.Split('\\')) {
                string part = segment.ToLowerInvariant();
                if (part == "appdata" || part == "program files" || part == "program files (x86)" || part == "programdata" || part == "windows" || part == "packages" || part == "software" || part == "apps" || part == "model" || part == "models" || part == "env" || part == "envs") return false;
            }
            try {
                // Check all ancestors as well, so selecting a nested source-project directory cannot bypass the boundary.
                for (string dir = full; dir != null && dir.Length > 3; dir = Path.GetDirectoryName(dir)) {
                    foreach (string marker in new[] { ".git", "node_modules", "pyvenv.cfg", "package.json", "pyproject.toml", "Cargo.toml", "go.mod", "CMakeLists.txt" })
                        if (File.Exists(Path.Combine(dir, marker)) || Directory.Exists(Path.Combine(dir, marker))) return false;
                    if (String.Equals(dir.TrimEnd('\\'), policy.ProfileRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) break;
                }
            } catch (Exception e) { if (!ScanWalker.IsFileError(e)) throw; return false; }
            return true;
        }
        static bool SoftwareFile(string path) {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return new[] { ".exe", ".dll", ".msi", ".sys", ".vhd", ".vhdx", ".safetensors", ".gguf", ".ckpt", ".pt", ".pth", ".onnx", ".sln", ".csproj", ".vbproj", ".py", ".js", ".ts", ".jsx", ".tsx", ".cs", ".java", ".c", ".cpp", ".h", ".go", ".rs" }.Contains(ext);
        }
        string Hash(FileRecord file, bool partial, CancellationToken token, ScanReport report) {
            try {
                token.ThrowIfCancellationRequested();
                using(var parents=new NativeFiles.ParentLocks(file.Path)) {
                FileStamp before = policy.Capture(file.Path, file.Root);
                if (before.Links != 1 || before.Length != file.Length || before.LastWriteTicks != file.LastWriteTicks || (file.Stamp != null && !Same(file.Stamp, before))) { report.Skipped++; return null; }
                byte[] result;
                using (var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
                using (var sha = SHA256.Create()) {
                    if(!Same(before,NativeFiles.Stamp(stream.SafeFileHandle))) { report.Skipped++; return null; }
                    byte[] buffer = new byte[65536];
                    if (partial) {
                        int count = ReadSample(stream, buffer, token); sha.TransformBlock(buffer, 0, count, buffer, 0);
                        if (stream.Length > buffer.Length) { stream.Seek(Math.Max(buffer.Length, stream.Length - buffer.Length), SeekOrigin.Begin); count = ReadSample(stream, buffer, token); sha.TransformBlock(buffer, 0, count, buffer, 0); }
                    } else {
                        int count; while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, count, buffer, 0); }
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0); result = sha.Hash;
                    FileStamp after = policy.Capture(file.Path, file.Root);
                    if (!Same(before, after)) { report.Skipped++; return null; }
                    file.Stamp = after;
                }
                string encoded=Convert.ToBase64String(result);if(!partial)file.ContentHash=encoded;return encoded;
                }
            } catch (Exception e) { if (!ScanWalker.IsFileError(e)) throw; report.Skipped++; return null; }
        }
        static int ReadSample(Stream stream, byte[] buffer, CancellationToken token) {
            int total = 0, read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) != 0) { token.ThrowIfCancellationRequested(); total += read; }
            return total;
        }
        static bool Same(FileStamp a, FileStamp b) {
            return a.Volume == b.Volume && a.FileId == b.FileId && a.Length == b.Length && a.LastWriteTicks == b.LastWriteTicks && a.Links == 1 && b.Links == 1 && String.Equals(a.FinalPath, b.FinalPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
