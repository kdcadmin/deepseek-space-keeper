using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClearDisk {
 public static class GuiSmoke {
  static void Check(bool value,string text) {if(!value)throw new Exception("GUI smoke assertion failed: "+text);}
  static IEnumerable<T> ControlsOf<T>(Control root) where T:Control {foreach(Control c in root.Controls){if(c is T)yield return (T)c;foreach(T nested in ControlsOf<T>(c))yield return nested;}}
  static void Pump(Task task) {DateTime end=DateTime.UtcNow.AddSeconds(12);while(!task.IsCompleted&&DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(10);}Check(task.IsCompleted,"background operation completed without blocking the UI");if(task.IsFaulted)throw task.Exception;Application.DoEvents();}
  public static int Run(string outputDir) {
   outputDir=Path.GetFullPath(outputDir);Directory.CreateDirectory(outputDir);
   string fixture=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","tests","fixtures","ui-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(fixture);
   string cache=Path.Combine(fixture,"download-cache.tmp"),personal=Path.Combine(fixture,"family-photo.jpg");File.WriteAllText(cache,"UI smoke fixture only; never deleted by this test.");File.WriteAllText(personal,"Personal file protection fixture.");
   var report=new ScanReport {Mode="cache",Root=fixture,ScannedFiles=4812,LogicalBytes=3573542912,Skipped=34,Seconds=2.4};
   report.Files.Add(new FileRecord {Path=cache,RuleId="smoke-cache",CanDelete=true,Risk=RiskLevel.Safe,Length=183271424,Category="浏览器下载缓存",Reason="可重新下载的缓存；清理后首次加载可能变慢。"});
   report.Files.Add(new FileRecord {Path=personal,CanDelete=false,CanRecycle=false,Risk=RiskLevel.Protected,Length=268435456,Category="个人文件（仅检查）",Reason="个人照片受到保护，请打开文件位置自行检查。"});
   report.Files.Add(new FileRecord {Path=Path.Combine(fixture,"retained-document.pdf"),CanDelete=true,Risk=RiskLevel.Safe,Length=7340032,IsKeeper=true,DuplicateGroup="duplicate-1",Category="重复个人文件",Reason="内容相同，保留项受到保护；本版本不自动删除。"});
   report.Folders.Add(new FolderUsage {Path=@"C:\Users\当前用户\.cache\huggingface",Bytes=2147483648,Files=312});report.Folders.Add(new FolderUsage {Path=fixture,Bytes=458947200,Files=3});
   report.Warnings.Add("模型、环境及共享包仅展示占用，请使用对应软件的管理工具处理。");report.Warnings.Add("已跳过 34 个受保护或无法访问的项目；统计为已遍历文件的逻辑大小。");
   using(var form=new MainForm()) {
    form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-2000,-2000);form.Show();Application.DoEvents();
    form.LoadReport(report);form.SelectEligible();Check(form.SelectedCount==1,"select-all selects eligible caches only");
    DataGridView grid=ControlsOf<DataGridView>(form).First(x=>x.VirtualMode);grid.CurrentCell=grid.Rows[0].Cells[0];Check(!grid.BeginEdit(false),"protected checkbox cannot be edited");grid.CurrentCell=grid.Rows[2].Cells[0];Check(!grid.BeginEdit(false),"duplicate keeper checkbox cannot be edited");
    form.SetSearch("个人照片");Check(form.VisibleCount==1,"reason search filters the live virtualized grid");form.SelectEligible();Check(form.SelectedCount==1,"filter cannot select protected files");
    form.SetSearch("浏览器");Check(form.VisibleCount==1,"category search filters grid");form.SetSearch("download-cache");Check(form.VisibleCount==1,"path search filters grid");
    form.SetSearch("not-present-9341");Check(form.VisibleCount==0,"no-match state");form.SetSearch("");
    using(Form preview=form.CreatePreview()){preview.CreateControl();var previewGrid=ControlsOf<DataGridView>(preview).Single();Check(previewGrid.Columns.Contains("Category")&&previewGrid.Columns.Contains("RuleId")&&previewGrid.Columns.Contains("Risk"),"preview shows category, rule and risk for every row");Check(grid.Columns.Contains("RuleId"),"main file grid exposes cache rule identity");Check(previewGrid.VirtualMode,"preview uses virtual rows for large cache sets");Check(Convert.ToString(previewGrid.Rows[0].Cells["Category"].Value)=="浏览器下载缓存","preview renders category value");Check(Convert.ToString(previewGrid.Rows[0].Cells["RuleId"].Value)=="smoke-cache","preview renders rule identity");Check(Convert.ToString(previewGrid.Rows[0].Cells["Risk"].Value).Contains("缓存"),"preview renders risk and handling");Check(previewGrid.Rows.Count==1,"preview contains every selected cache path");Check(Convert.ToString(previewGrid.Rows[0].Cells["Path"].Value)==cache,"preview preserves full path");Check(preview.AcceptButton is Button&&((Button)preview.AcceptButton).DialogResult==DialogResult.Cancel,"Enter defaults to return, not delete");Check(ControlsOf<Button>(preview).Any(x=>x.Text=="确认清理"),"explicit confirmation available");}
    Application.DoEvents();using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(outputDir,"ui.png"),ImageFormat.Png);}
    form.LoadReport(new ScanReport {Mode="disk",Files={new FileRecord {Path=personal,CanDelete=true,CanRecycle=true,Risk=RiskLevel.Safe,Length=1}}});form.SelectEligible();Check(form.SelectedCount==0,"disk inspection cannot select personal files even if flags are inconsistent");
    form.LoadReport(new ScanReport {Mode="cache"});Check(form.StateText.Contains("没有可显示项目"),"empty scan has distinct state");
    Task scan=form.RunScan("取消测试",delegate(CancellationToken t,Action<ScanProgress> p){t.WaitHandle.WaitOne(5000);return new Scanner(SafetyPolicy.ForCurrentUser()).ScanDisk(fixture,t,p);});Check(form.IsBusy,"scan runs asynchronously");form.CancelOperation();Pump(scan);Check(!form.IsBusy&&form.StateText.Contains("已取消"),"cancelled scan has distinct state");
    form.Close();Check(form.IsDisposed,"idle close disposes form");
   }
   using(var form=new MainForm()) {
    form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-2000,-2000);form.Show();Application.DoEvents();
    Task task=form.RunScan("关闭测试",delegate(CancellationToken t,Action<ScanProgress> p){t.WaitHandle.WaitOne(5000);return new ScanReport {Mode="cache",Cancelled=t.IsCancellationRequested};});form.Close();Check(!form.IsDisposed,"active close waits for background completion");Pump(task);Check(form.IsDisposed,"form closes after cancelled worker finishes");
   }
   Check(File.Exists(cache)&&File.Exists(personal),"smoke does not delete fixture files");
   File.WriteAllText(Path.Combine(outputDir,"smoke-result.txt"),"PASS\r\nReal native form rendered: ui.png\r\nSelection: eligible caches only; protected and keeper checkboxes locked.\r\nSearch: category, reason, path, no-match.\r\nPreview: all selected full paths, explicit confirmation, Enter defaults to return.\r\nPersonal inspection: delete/recycle flags disabled.\r\nStates: empty, cancellation; active close cancels and waits asynchronously.\r\nNo cleanup confirmed or performed.\r\nFixture preserved: "+fixture+"\r\n",new System.Text.UTF8Encoding(true));
   V2GuiSmoke.Run(outputDir);return 0;
  }
 }
}




