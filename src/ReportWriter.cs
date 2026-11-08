using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClearDisk {
    public static class ReportWriter {
        static string Csv(string value) { return "\""+(value??"").Replace("\"","\"\"")+"\""; }
        public static void WriteScan(string path,ScanReport report) {
            WriteScan(path,report,false);
        }
        internal static void WriteScanNew(string path,ScanReport report) {WriteScan(path,report,true);}
        static void WriteScan(string path,ScanReport report,bool createNew) {
            using(var output=new FileStream(path,createNew?FileMode.CreateNew:FileMode.Create,FileAccess.Write,FileShare.None))
            using(var writer=new StreamWriter(output,new UTF8Encoding(true))) {
            if(Path.GetExtension(path).Equals(".csv",StringComparison.OrdinalIgnoreCase)) {
                    writer.WriteLine("分类,类型,逻辑大小字节,允许清理,原因,路径");
                    foreach(var file in report.Files) writer.WriteLine(String.Join(",",new[]{Csv(file.Category),Csv(Display.Risk(file.Risk)),file.Length.ToString(),Csv(file.CanDelete?"是":"否"),Csv(file.Reason),Csv(file.Path)}));
                return;
            }
            // Snapshots and file identities are internal; exports cannot be re-imported for deletion.
            var serializer=new JavaScriptSerializer {MaxJsonLength=Int32.MaxValue};
            var data=new {Version="2.0.0",Kind="只读扫描报告",report.Mode,report.Root,report.StartedUtc,report.Seconds,
                report.ScannedFiles,report.LogicalBytes,report.Skipped,report.Cancelled,report.Incomplete,
                EligibleCacheLogicalBytes=report.Files.Where(f=>f.CanDelete).Sum(f=>f.Length),
                OrganizablePersonalLogicalBytes=report.Files.Where(f=>f.CanOrganize).Sum(f=>f.Length),
                Note="逻辑大小不等于可释放空间；硬链接、保护范围、跳过和后台磁盘写入均会影响统计。报告不能导入后删除文件。",
                Warnings=report.Warnings, Folders=report.Folders,
                Files=report.Files.Select(f=>new {f.Path,f.Category,f.RuleId,f.Reason,Risk=Display.Risk(f.Risk),f.Length,LastWriteUtc=f.LastWriteTicks>0?new DateTime(f.LastWriteTicks,DateTimeKind.Utc).ToString("o"):null,f.CanDelete,f.CanRecycle,f.CanOrganize,f.DuplicateGroup,f.IsKeeper}).ToArray()};
            writer.Write(serializer.Serialize(data));
            }
        }
        public static void WriteClean(string path,CleanReport report) {
            var serializer=new JavaScriptSerializer {MaxJsonLength=Int32.MaxValue};
            File.WriteAllText(path,serializer.Serialize(new {Version="2.0.0",Kind="清理执行报告",Report=report,
                MeasuredFreeSpaceChange=report.FreeAfter-report.FreeBefore,Note="可用空间差值也可能受到其他进程写入影响；移入隔离区不释放空间，彻底删除后无法恢复。"}),new UTF8Encoding(true));
        }
    }
}
