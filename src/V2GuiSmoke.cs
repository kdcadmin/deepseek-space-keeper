using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ClearDisk {
 internal static class V2GuiSmoke {
  static void Check(bool ok,string text){if(!ok)throw new Exception("V2 GUI: "+text);}
  static IEnumerable<T> ControlsOf<T>(Control parent) where T:Control {foreach(Control c in parent.Controls){if(c is T)yield return (T)c;foreach(T nested in ControlsOf<T>(c))yield return nested;}}
  internal static void Run(string output){
   string fixture=Path.GetFullPath(Path.Combine("tests","fixtures","v2-ui-"+Guid.NewGuid().ToString("N"),"profile"));string docs=Path.Combine(fixture,"Documents");Directory.CreateDirectory(docs);
   string a=Path.Combine(docs,"old-large-photo.jpg"),b=Path.Combine(docs,"recent-small-note.txt");File.WriteAllText(a,new string('a',8192));File.SetLastWriteTimeUtc(a,DateTime.UtcNow.AddDays(-100));File.WriteAllText(b,"small");
   var policy=new SafetyPolicy(fixture,Path.Combine(fixture,"app"));var report=new Scanner(policy).ScanDisk(docs,System.Threading.CancellationToken.None,null);
   using(var form=new MainForm(policy)){
    form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-2200,-2200);form.Show();Application.DoEvents();form.LoadReport(report);
    form.SelectEligible();Check(form.SelectedCount==0,"personal files are never selected by cache select-all");
    form.SetExtraFilters(30,1024);Check(form.VisibleCount==1,"age and size filters combine");form.SetExtraFilters(0,0);
    form.SetOrganizationMode(true);Check(form.SelectedCount==0,"entering organization mode selects nothing");form.SelectEligible();Check(form.SelectedCount==0,"no personal select-all");
    form.SetManualSelection(a,true);Check(form.SelectedCount==1,"explicit personal selection allowed for verified file");
    using(var entered=new System.Threading.ManualResetEventSlim())using(var release=new System.Threading.ManualResetEventSlim()){
     var blocker=System.Threading.Tasks.Task.Run(()=>{lock(policy.OrganizationGate){entered.Set();release.Wait(3000);}});Check(entered.Wait(1000),"organization executor lock held in worker");
     try{var timer=System.Diagnostics.Stopwatch.StartNew();form.Invalidate(true);form.Update();using(var bmp=new Bitmap(form.Width,form.Height))form.DrawToBitmap(bmp,new Rectangle(Point.Empty,form.Size));Check(timer.ElapsedMilliseconds<1500,"UI paint never waits for an active file hash lock");}finally{release.Set();blocker.Wait();}
    }
    using(var preview=form.CreateOrganizationPreview())Check(preview.AcceptButton is Button&&((Button)preview.AcceptButton).DialogResult==DialogResult.Cancel,"organization confirmation defaults to cancel");
    form.SetOrganizationMode(false);Check(form.SelectedCount==0,"mode change clears selection");
    var cacheReport=new ScanReport {Mode="cache"};cacheReport.Files.Add(new FileRecord {Path=a,Root=docs,Category="缓存",CanDelete=true,Risk=RiskLevel.Safe,Length=8192});cacheReport.Folders.Add(new FolderUsage{Path=docs,Bytes=8192,Files=1});form.LoadReport(cacheReport);var summary=ControlsOf<DataGridView>(form).Single(x=>x.Columns.Contains("Cleanable"));Check(Convert.ToString(summary.Rows[0].Cells["Cleanable"].Value)=="8 KB","summary displays current cache eligibility");form.ReconcileRemoved(new HashSet<string>(new[]{a},StringComparer.OrdinalIgnoreCase));Check(Convert.ToString(summary.Rows[0].Cells["Cleanable"].Value)=="0 B","summary removes successfully cleaned cache bytes");
    using(var catalog=ExtendedDialogs.CreateCatalog()){catalog.CreateControl();Check(catalog.Text.Contains("4,068"),"full embedded rule catalog is available in shipped exe");}
    form.LoadReport(new ScanReport {Mode="community",Files={new FileRecord {Path=a,CanOrganize=true,CanDelete=true,Risk=RiskLevel.Safe}}});form.SetOrganizationMode(true);form.SetManualSelection(a,true);Check(form.SelectedCount==0,"community results cannot gain mutation authority from flags");
    form.LoadReport(report);form.SetOrganizationMode(true);form.SetManualSelection(a,true);Application.DoEvents();using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(Point.Empty,form.Size));bmp.Save(Path.Combine(output,"v2-ui.png"),ImageFormat.Png);}
    form.Close();
   }
   Check(File.Exists(a)&&File.Exists(b),"GUI smoke never moves or deletes files");
   File.WriteAllText(Path.Combine(output,"v2-smoke-result.txt"),"PASS: combined age/size filters; explicit personal mode and selection; no personal select-all; cancel-default preview; 4068-rule catalog; community mutation gate; no live cleanup.");
  }
 }
}
