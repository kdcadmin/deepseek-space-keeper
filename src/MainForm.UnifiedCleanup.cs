using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace ClearDisk {
 public sealed partial class MainForm {
  TableLayoutPanel mainLayout;
  StructuredCleanupForm cleanupWorkspace;
  Panel cleanupHost;
  readonly List<Control> legacyWorkspace=new List<Control>();
  RowStyle[] legacyRows;
  internal Func<StructuredCleanupForm> CleanupWorkspaceFactory=()=>new StructuredCleanupForm(false);
  bool CleanupVisible {get{return cleanupHost!=null&&cleanupHost.Visible;}}

  internal MainForm(SafetyPolicy safety,bool autoFullScan):this(safety){if(autoFullScan)Shown+=async delegate{await OpenCompatibleCleanup(true);};}
  async Task OpenCompatibleCleanup(bool scanAll){
   if(busy)return;LeaveHome();
   selected.Clear();UpdateSelection();
   if(cleanupWorkspace==null){
    var view=CleanupWorkspaceFactory();
    if(view==null)throw new InvalidOperationException("无法创建完整扫描结果区。");
    cleanupWorkspace=view;view.ConfigureEmbedded();
    legacyWorkspace.AddRange(mainLayout.Controls.Cast<Control>().Where(c=>mainLayout.GetRow(c)>=2));
    legacyRows=mainLayout.RowStyles.Cast<RowStyle>().Select(r=>new RowStyle(r.SizeType,r.Height)).ToArray();
    cleanupHost=new Panel{Name="mainCleanupWorkspace",Dock=DockStyle.Fill,Margin=Padding.Empty,Visible=false};
    mainLayout.Controls.Add(cleanupHost,0,2);mainLayout.SetRowSpan(cleanupHost,6);
    cleanupHost.Controls.Add(view);view.WorkingChanged+=CleanupWorkingChanged;
   }
   ShowCleanupWorkspace();
   if(scanAll)await cleanupWorkspace.ScanAll();
  }
  void ShowCleanupWorkspace(){
   mainLayout.SuspendLayout();
   foreach(var c in legacyWorkspace)c.Visible=false;
   for(int i=2;i<mainLayout.RowStyles.Count;i++){mainLayout.RowStyles[i].SizeType=i==3?SizeType.Percent:SizeType.Absolute;mainLayout.RowStyles[i].Height=i==3?100:0;}
   cleanupHost.Visible=true;cleanupWorkspace.Show();cleanupHost.BringToFront();mainLayout.ResumeLayout(true);
  }
  void HideCleanupWorkspace(){
   if(!CleanupVisible)return;
   if(cleanupWorkspace.IsWorking)throw new InvalidOperationException("请等待当前完整扫描或清理完成。");
   cleanupWorkspace.DiscardSelection();mainLayout.SuspendLayout();cleanupHost.Visible=false;
   foreach(var c in legacyWorkspace)c.Visible=true;
   for(int i=2;i<legacyRows.Length;i++){mainLayout.RowStyles[i].SizeType=legacyRows[i].SizeType;mainLayout.RowStyles[i].Height=legacyRows[i].Height;}
   mainLayout.ResumeLayout(true);
  }
  void CleanupWorkingChanged(){
   if(IsDisposed||!CleanupVisible)return;
   canCancel=cleanupWorkspace.CanCancelWork;SetBusy(cleanupWorkspace.IsWorking);cancel.Enabled=busy&&canCancel;if(pet!=null)pet.SetPose(busy,cleanupWorkspace.IsCleaning);
   if(!busy){UpdateDrive();if(closeRequested&&IsHandleCreated)BeginInvoke(new Action(Close));}
  }
 }
}
