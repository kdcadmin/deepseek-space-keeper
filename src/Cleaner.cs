using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ClearDisk {
    public sealed class Cleaner {
        readonly SafetyPolicy policy;
        public Cleaner(SafetyPolicy policy) { this.policy=policy; }
        public CleanReport Execute(IEnumerable<FileRecord> files,bool recycle,CancellationToken token,Action<ScanProgress> progress) {
            var report=new CleanReport();
            report.FreeBefore=new DriveInfo("C:\\").AvailableFreeSpace;
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime last=DateTime.MinValue;
            foreach(var file in files) {
                if(token.IsCancellationRequested) { report.Cancelled=true; break; }
                if(file==null) continue;
                string normalized;
                try { normalized=NativeFiles.Normalize(file.Path); }
                catch(Exception ex) { report.Results.Add(new CleanResult {Path=file.Path,Status="跳过",Reason=ex.Message}); continue; }
                if(!seen.Add(normalized)) continue;
                string reason; bool failed=false;
                bool ok=recycle ? policy.TryRecycle(file,out reason) : policy.TryDelete(file,out reason,out failed);
                report.Results.Add(new CleanResult {Path=file.Path, Action=recycle ? "移入回收站" : "删除缓存", Status=ok ? "成功" : failed ? "失败" : "跳过",Reason=reason,Bytes=ok ? file.Length : 0});
                if(progress!=null && (DateTime.UtcNow-last).TotalMilliseconds>=200) {
                    progress(new ScanProgress {Message="正在核验并处理所选文件",Files=report.Results.Count}); last=DateTime.UtcNow;
                }
            }
            report.FreeAfter=new DriveInfo("C:\\").AvailableFreeSpace;
            return report;
        }
    }
}
