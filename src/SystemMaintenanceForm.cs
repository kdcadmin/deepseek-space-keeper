using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClearDisk {
 internal sealed class SystemMaintenanceForm:Form {
  readonly SystemMaintenanceEngine engine;readonly Func<string,bool> confirm;readonly Action elevate;
  readonly CheckedListBox actions=new CheckedListBox();readonly TextBox output=new TextBox();readonly Label status=new Label();
  readonly Button previewButton=new Button(),executeButton=new Button(),cancelButton=new Button(),elevateButton=new Button();
  MaintenancePlan preview;CancellationTokenSource cancellation;bool busy;
  internal bool HasPreview {get{return preview!=null;}}internal bool IsWorking {get{return busy;}}
  internal SystemMaintenanceForm():this(new SystemMaintenanceEngine(),null,null){}
  internal SystemMaintenanceForm(SystemMaintenanceEngine engine,Func<string,bool> confirm):this(engine,confirm,null){}
  internal SystemMaintenanceForm(SystemMaintenanceEngine engine,Func<string,bool> confirm,Action elevate){
   this.engine=engine;this.confirm=confirm??ConfirmDetails;this.elevate=elevate??OpenElevated;
   Text="系统维护 · 预览后执行";Size=new Size(1050,780);MinimumSize=new Size(820,620);StartPosition=FormStartPosition.CenterParent;Font=new Font("Microsoft YaHei UI",9);
   var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(14),ColumnCount=1,RowCount=5};
   layout.RowStyles.Add(new RowStyle(SizeType.Absolute,70));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,205));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));Controls.Add(layout);
   layout.Controls.Add(new Label{Dock=DockStyle.Fill,Text="按需勾选，然后预览、阅读具体影响并确认。所有操作默认不勾选。\r\n本页运行 Windows 支持的维护接口；无法保证释放大小，不自动重启。清除回收站和日志不可撤销。\r\n管理员窗口也会保持空闲，需重新选择并预览。",AutoSize=false},0,0);
   actions.Name="maintenanceActions";actions.Dock=DockStyle.Fill;actions.CheckOnClick=true;foreach(var action in SystemMaintenanceCatalog.Actions)actions.Items.Add(action,false);
   actions.ItemCheck+=(s,e)=>{ClearPreview();};layout.Controls.Add(actions,0,1);
   var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill};
   previewButton.Name="maintenancePreview";previewButton.Text="预览所选操作";previewButton.AutoSize=true;previewButton.Click+=(s,e)=>PreviewSelection();
   executeButton.Name="maintenanceExecute";executeButton.Text="确认并执行";executeButton.AutoSize=true;executeButton.Enabled=false;executeButton.Click+=async(s,e)=>await ExecutePreviewAsync();
   cancelButton.Name="maintenanceCancel";cancelButton.Text="当前项结束后停止";cancelButton.AutoSize=true;cancelButton.Enabled=false;cancelButton.Click+=(s,e)=>CancelAfterCurrent();
   elevateButton.Name="maintenanceElevate";elevateButton.Text="以管理员打开空闲窗口";elevateButton.AutoSize=true;elevateButton.Click+=(s,e)=>{try{this.elevate();}catch(Exception ex){status.Text="管理员窗口未打开："+ex.Message;}};
   buttons.Controls.AddRange(new Control[]{previewButton,executeButton,cancelButton,elevateButton});layout.Controls.Add(buttons,0,2);
   output.Name="maintenanceOutput";output.Dock=DockStyle.Fill;output.Multiline=true;output.ReadOnly=true;output.ScrollBars=ScrollBars.Both;output.WordWrap=false;layout.Controls.Add(output,0,3);
   status.Name="maintenanceStatus";status.Dock=DockStyle.Fill;status.Text="尚未预览；未执行操作。";layout.Controls.Add(status,0,4);
   AcceptButton=null;FormClosing+=(s,e)=>{if(busy){e.Cancel=true;status.Text="操作正在执行；请等待 Windows 当前操作结束。可请求结束后停止。";}};
   try{elevateButton.Visible=!engine.Context().IsAdministrator;}catch(Exception ex){status.Text="无法查询权限："+ex.Message;}
  }
  internal void SelectActions(params MaintenanceActionId[] ids){if(busy)throw new InvalidOperationException("维护正在执行。");foreach(var id in ids)SystemMaintenanceCatalog.Find(id);for(int i=0;i<actions.Items.Count;i++)actions.SetItemChecked(i,ids.Contains(((MaintenanceAction)actions.Items[i]).Id));ClearPreview();}
  void ClearPreview(){preview=null;engine.InvalidatePreview();executeButton.Enabled=false;if(!busy)status.Text="选择已变化，请重新预览。";}
  internal void PreviewSelection(){if(busy)throw new InvalidOperationException("维护正在执行。");ClearPreview();try{preview=engine.Preview(actions.CheckedItems.Cast<MaintenanceAction>().Select(a=>a.Id));output.Text=preview.ConfirmationText;executeButton.Enabled=preview.Context.IsAdministrator;status.Text=preview.Context.IsAdministrator?"预览完成。估计可释放空间：未知；请阅读并确认。":"当前没有管理员权限。以管理员打开空闲窗口后重新选择并预览。";}catch(Exception ex){status.Text="无法预览："+ex.Message;}}
  internal async Task ExecutePreviewAsync(){
   if(busy||preview==null)return;var plan=preview;
   if(!confirm(plan.ConfirmationText))return;
   preview=null;executeButton.Enabled=false;busy=true;actions.Enabled=false;previewButton.Enabled=false;elevateButton.Enabled=false;cancelButton.Enabled=true;cancellation=new CancellationTokenSource();status.Text="正在执行；不会强制中止 Windows 操作。";output.Clear();
   try{
    var progress=new Progress<string>(AppendOutput);
    var result=await Task.Run(()=>engine.Execute(plan,true,cancellation.Token,progress));
    foreach(var action in result.Actions){AppendOutput("\r\n"+SystemMaintenanceCatalog.Find(action.Id).Name+": "+action.Status+", exit="+(action.ExitCode.HasValue?action.ExitCode.Value.ToString():"未启动/异常")+"\r\n");AppendOutput(action.StandardOutput+"\r\n"+action.StandardError+"\r\n");}
    string delta=result.ObservedChange.HasValue?result.ObservedChange.Value.ToString("N0")+" 字节":"未知";
    status.Text="结果："+result.Status+"；C 盘可用空间观察差："+delta+"（含其他程序变化）"+(result.RestartRequired?"；需要重启；未自动重启":"")+"。再次执行需重新预览。";
   }catch(Exception ex){status.Text="未完成："+ex.Message;AppendOutput(ex.ToString());}
   finally{busy=false;actions.Enabled=true;previewButton.Enabled=true;elevateButton.Enabled=true;cancelButton.Enabled=false;cancellation.Dispose();cancellation=null;engine.InvalidatePreview();}
  }
  internal void CancelAfterCurrent(){if(cancellation!=null){cancellation.Cancel();status.Text="已请求停止；正在等待当前 Windows 操作结束。";cancelButton.Enabled=false;}}
  void AppendOutput(string message){const int maximum=180000;if(IsDisposed)return;int remaining=maximum-output.TextLength;if(remaining>0)output.AppendText(message.Length<=remaining?message:message.Substring(0,remaining));}
  static void OpenElevated(){Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--system-maintenance-window"){UseShellExecute=true,Verb="runas",WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory});}
  bool ConfirmDetails(string text){using(var dialog=new Form{Text="确认具体操作与影响",Size=new Size(920,650),MinimumSize=new Size(650,420),StartPosition=FormStartPosition.CenterParent,Font=Font}){
   var details=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,Text=text,WordWrap=false};var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=48,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(8)};
   var no=new Button{Text="取消",DialogResult=DialogResult.Cancel,AutoSize=true};var yes=new Button{Text="已阅读，执行以上操作",DialogResult=DialogResult.OK,AutoSize=true};buttons.Controls.Add(no);buttons.Controls.Add(yes);dialog.Controls.Add(details);dialog.Controls.Add(buttons);dialog.CancelButton=no;dialog.AcceptButton=null;return dialog.ShowDialog(this)==DialogResult.OK;
  }}
 }
}
