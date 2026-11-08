using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClearDisk {
 // This window has no cleanup executor or mutation controls. An elevated launch
 // runs this entry point only, to inspect otherwise inaccessible rule matches.
 internal sealed class ReferenceInventoryForm:Form {
  readonly SafetyPolicy policy;
  readonly Button start=new Button(),stop=new Button(),save=new Button();
  readonly Label status=new Label(),progress=new Label();
  readonly DataGridView categories=new DataGridView(),files=new DataGridView();
  readonly ListBox notes=new ListBox();readonly System.Windows.Forms.Timer pulse=new System.Windows.Forms.Timer();
  readonly object gate=new object();ScanProgress latest;ScanReport report;CancellationTokenSource cancellation;bool busy,closing;
  internal ReferenceInventoryForm(SafetyPolicy safety){
   policy=safety;Text="原版规则对照 · 只读扫描";Font=new Font("Microsoft YaHei UI",9);Size=new Size(1180,780);MinimumSize=new Size(980,650);StartPosition=FormStartPosition.CenterParent;
   var panel=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=1,RowCount=5};panel.RowStyles.Add(new RowStyle(SizeType.Absolute,65));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,45));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,45));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,40));Controls.Add(panel);
   panel.Controls.Add(new Label{Dock=DockStyle.Fill,Text="对照原版 251109 规则检查当前用户、Windows 和共享应用数据。\r\n这里只统计占用，不提供清理操作。各分类可能重叠，全局总量按文件身份去重；无权限项目记为未知。",TextAlign=ContentAlignment.MiddleLeft},0,0);
   var actions=new FlowLayoutPanel{Dock=DockStyle.Fill};foreach(var button in new[]{start,stop,save}){button.Size=new Size(145,32);actions.Controls.Add(button);}start.Text="开始只读扫描";stop.Text="取消扫描";save.Text="导出对照报告";stop.Enabled=false;save.Enabled=false;panel.Controls.Add(actions,0,1);
   status.Dock=DockStyle.Fill;status.Text="尚未扫描";status.TextAlign=ContentAlignment.MiddleLeft;panel.Controls.Add(status,0,2);
   var tabs=new TabControl{Dock=DockStyle.Fill};TabPage categoryTab=new TabPage("各规则匹配量"),fileTab=new TabPage("路径明细（最多2000项）"),noteTab=new TabPage("跳过与未知");tabs.TabPages.AddRange(new[]{categoryTab,fileTab,noteTab});SetGrid(categories);SetGrid(files);categories.Columns.Add("category","原版规则分类");categories.Columns.Add("size","匹配逻辑大小（可能重叠）");categories.Columns.Add("bytes","原始字节数");categories.Columns.Add("count","文件数");files.Columns.Add("path","完整路径");files.Columns.Add("category","规则分类");files.Columns.Add("bytes","逻辑字节数");categoryTab.Controls.Add(categories);fileTab.Controls.Add(files);notes.Dock=DockStyle.Fill;notes.HorizontalScrollbar=true;noteTab.Controls.Add(notes);panel.Controls.Add(tabs,0,3);progress.Dock=DockStyle.Fill;progress.TextAlign=ContentAlignment.MiddleLeft;panel.Controls.Add(progress,0,4);
   start.Click+=async delegate{await Scan();};stop.Click+=delegate{if(cancellation!=null)cancellation.Cancel();};save.Click+=delegate{Export();};
   pulse.Interval=180;pulse.Tick+=delegate{ScanProgress update;lock(gate){update=latest;latest=null;}if(update!=null){status.Text="已检查 "+update.Files.ToString("N0")+" 项 · "+Display.Size(update.Bytes);progress.Text=update.Message;}};
   FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy){closing=true;e.Cancel=true;cancellation.Cancel();}};FormClosed+=delegate{pulse.Dispose();};
  }
  static void SetGrid(DataGridView grid){grid.Dock=DockStyle.Fill;grid.ReadOnly=true;grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;grid.RowHeadersVisible=false;grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;grid.BackgroundColor=Color.White;}
  async Task Scan(){
   if(busy)return;busy=true;start.Enabled=false;save.Enabled=false;stop.Enabled=true;cancellation=new CancellationTokenSource();status.Text="正在按原版规则检查占用…";pulse.Start();
   try{
    report=await Task.Run(()=>CommunityScanner.CreateReferenceInventory(policy).Scan(cancellation.Token,p=>{lock(gate)latest=p;}));pulse.Stop();lock(gate)latest=null;
    LoadReport(report);
   }catch(Exception e){pulse.Stop();status.Text="对照扫描未完成";progress.Text=e.Message;}
   finally{pulse.Stop();lock(gate)latest=null;cancellation.Dispose();cancellation=null;busy=false;start.Enabled=true;stop.Enabled=false;save.Enabled=report!=null;if(closing)Close();}
  }
  internal void LoadReport(ScanReport value){
   report=value;categories.Rows.Clear();files.Rows.Clear();notes.Items.Clear();foreach(var item in report.Folders.OrderByDescending(f=>f.Bytes))categories.Rows.Add(item.Path,Display.Size(item.Bytes),item.Bytes.ToString("N0"),item.Files.ToString("N0"));foreach(var file in report.Files)files.Rows.Add(file.Path,file.Category,file.Length.ToString("N0"));foreach(string warning in report.Warnings)notes.Items.Add(warning);
   status.Text=(report.Cancelled?"已取消（结果不完整）":report.Incomplete||report.Skipped>0?"检查已结束（存在未知 / 未检查项）":"扫描完成")+" · 去重后的逻辑总量 "+Display.Size(report.LogicalBytes)+" · "+report.ScannedFiles.ToString("N0")+" 个文件 · 跳过/未知 "+report.Skipped.ToString("N0")+" 项";
   progress.Text="这不是可释放空间。与原版对照时请同时检查分类重叠、权限和扫描时间。";
  }
  internal static string ReportPath(SafetyPolicy safety,string path){if(String.IsNullOrWhiteSpace(path)||!path.StartsWith(@"C:\",StringComparison.OrdinalIgnoreCase))throw new IOException("请选择C盘绝对报告路径。");path=NativeFiles.Normalize(path);if(!SafetyPolicy.IsWithin(path,safety.ProfileRoot)||!Path.GetExtension(path).Equals(".json",StringComparison.OrdinalIgnoreCase))throw new IOException("对照报告仅保存为当前用户目录下的新 JSON 文件。");return path;}
  internal void ExportTo(string path,ScanReport value){path=ReportPath(policy,path);using(var parents=new NativeFiles.ParentLocks(path))ReportWriter.WriteScanNew(path,value);}
  void Export(){if(report==null||busy)return;using(var picker=new SaveFileDialog{Title="保存新的只读对照报告（不覆盖已有文件）",Filter="JSON (*.json)|*.json",InitialDirectory=policy.ProfileRoot,FileName="原版规则对照-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json"})if(picker.ShowDialog(this)==DialogResult.OK)try{ExportTo(picker.FileName,report);progress.Text="报告已保存："+picker.FileName;}catch(Exception e){MessageBox.Show(this,e.Message,Text);}}
  internal static void OpenElevated(IWin32Window owner){
   try{Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,"--readonly-reference-window"){UseShellExecute=true,Verb="runas"});}
   catch(System.ComponentModel.Win32Exception e){if(e.NativeErrorCode!=1223)MessageBox.Show(owner,"只读扫描窗口未打开："+e.Message,"原版规则对照");}
  }
 }
}
