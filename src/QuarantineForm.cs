using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClearDisk {
 internal sealed class QuarantineForm : Form {
  readonly QuarantineStore store;
  readonly DataGridView grid=new DataGridView();
  readonly Label status=new Label();
  readonly Button refresh=new Button(),restore=new Button(),purge=new Button(),cancel=new Button();
  readonly HashSet<string> selected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  List<QuarantineEntry> entries=new List<QuarantineEntry>();
  CancellationTokenSource cancellation;bool busy,closeRequested;
  internal QuarantineForm(SafetyPolicy policy){
   store=new QuarantineStore(policy);Text="隔离区 / 恢复 · 隔离仍占用 C 盘";Font=new Font("Microsoft YaHei UI",9);Size=new Size(1120,740);MinimumSize=new Size(900,560);StartPosition=FormStartPosition.CenterParent;
   var panel=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=4};panel.RowStyles.Add(new RowStyle(SizeType.Absolute,70));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,45));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,60));Controls.Add(panel);
   panel.Controls.Add(new Label {Text="隔离文件完整保留在当前用户目录，没有释放磁盘空间。请逐项核对，选择恢复或另行确认彻底删除。\r\n恢复只回到原位置，原位置有新文件时会跳过；记录损坏或文件已变化时不处理。彻底删除无法撤销。\r\n隔离位置："+store.Root,Dock=DockStyle.Fill},0,0);
   grid.Dock=DockStyle.Fill;grid.BackgroundColor=Color.White;grid.AllowUserToAddRows=false;grid.RowHeadersVisible=false;grid.VirtualMode=true;grid.AutoGenerateColumns=false;grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;
   grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="Selected",HeaderText="选择",Width=55});
   foreach(var spec in new[]{new[]{"State","状态","130"},new[]{"Bytes","逻辑大小","110"},new[]{"Path","完整原路径","480"},new[]{"Reason","说明","550"}})grid.Columns.Add(new DataGridViewTextBoxColumn{Name=spec[0],HeaderText=spec[1],Width=Int32.Parse(spec[2]),ReadOnly=true});
   grid.CellValueNeeded+=delegate(object s,DataGridViewCellValueEventArgs e){var x=entries[e.RowIndex];e.Value=e.ColumnIndex==0?(object)selected.Contains(x.ReceiptPath):e.ColumnIndex==1?x.State:e.ColumnIndex==2?Display.Size(x.Length):e.ColumnIndex==3?x.OriginalPath:x.Reason;};
   grid.CellValuePushed+=delegate(object s,DataGridViewCellValueEventArgs e){var x=entries[e.RowIndex];if(e.ColumnIndex==0&&!busy&&x.State=="可恢复"&&Convert.ToBoolean(e.Value))selected.Add(x.ReceiptPath);else selected.Remove(x.ReceiptPath);UpdateButtons();};
   grid.CellBeginEdit+=delegate(object s,DataGridViewCellCancelEventArgs e){e.Cancel=busy||e.ColumnIndex!=0||entries[e.RowIndex].State!="可恢复";};grid.CurrentCellDirtyStateChanged+=delegate {if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};panel.Controls.Add(grid,0,1);
   var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill};refresh.Text="刷新记录";restore.Text="恢复到原位置…";purge.Text="彻底删除所选…";cancel.Text="取消任务";
   foreach(var b in new[]{refresh,restore,purge,cancel}){b.Size=new Size(160,33);buttons.Controls.Add(b);}panel.Controls.Add(buttons,0,2);status.Dock=DockStyle.Fill;status.TextAlign=ContentAlignment.MiddleLeft;panel.Controls.Add(status,0,3);
   refresh.Click+=async delegate {await Reload();};restore.Click+=async delegate {await Execute(false);};purge.Click+=async delegate {await Execute(true);};cancel.Click+=delegate {if(cancellation!=null)cancellation.Cancel();};Shown+=async delegate {await Reload();};
   FormClosing+=delegate(object s,FormClosingEventArgs e){if(busy){e.Cancel=true;closeRequested=true;cancellation.Cancel();status.Text="正在停止，当前文件操作结束后关闭…";}};UpdateButtons();
  }
  void UpdateButtons(){refresh.Enabled=!busy;restore.Enabled=purge.Enabled=!busy&&selected.Count>0;cancel.Enabled=busy;grid.Enabled=!busy;}
  void Begin(string text){busy=true;cancellation=new CancellationTokenSource();status.Text=text;UpdateButtons();}
  void End(){busy=false;cancellation.Dispose();cancellation=null;UpdateButtons();if(closeRequested)Close();}
  async Task Reload(){
   if(busy)return;Begin("正在核验隔离记录和文件身份…");
   try {var list=await Task.Run(()=>store.List(cancellation.Token));entries=list.OrderByDescending(x=>x.State=="可恢复").ToList();selected.Clear();grid.RowCount=entries.Count;grid.Invalidate();status.Text="核验完成："+entries.Count+" 条记录，现存合格隔离文件 "+entries.Count(x=>x.State=="可恢复")+" 个。未移入 / 已恢复记录可作核对参考。";}
   catch(OperationCanceledException){status.Text="已取消记录读取。";}
   catch(Exception e){status.Text="无法读取隔离区："+e.Message;}
   finally {End();}
  }
  async Task Execute(bool permanent){
   if(busy)return;var chosen=entries.Where(x=>x.State=="可恢复"&&selected.Contains(x.ReceiptPath)).ToList();if(chosen.Count==0)return;
   string description=permanent?"将彻底删除以上 "+chosen.Count+" 个隔离文件，逻辑大小 "+Display.Size(chosen.Sum(x=>x.Length))+"。这一步无法撤销。\r\n只处理身份仍一致的隔离文件；实际释放以可用空间变化为准。":"将尝试把以上 "+chosen.Count+" 个隔离文件恢复到完整原路径。\r\n原位置已有文件、原目录受保护、身份变化或被占用时会跳过，绝不覆盖。";
   using(var form=ExtendedDialogs.CreateConfirmation(permanent?"彻底删除隔离文件":"恢复隔离文件",description,chosen.Select(x=>new[]{Display.Size(x.Length),x.State,x.Reason,x.OriginalPath}).ToList(),permanent?"确认彻底删除":"确认恢复",permanent))if(form.ShowDialog(this)!=DialogResult.OK)return;
   Begin(permanent?"正在逐项核验并彻底删除…":"正在逐项核验并恢复…");
   var results=new List<CleanResult>();long before=Free(),after=before;
   try {
    await Task.Run(()=>{foreach(var item in chosen){if(cancellation.IsCancellationRequested)break;string reason;bool ok;try{ok=permanent?store.Purge(item.ReceiptPath,out reason,cancellation.Token):store.Restore(item.ReceiptPath,out reason,cancellation.Token);}catch(OperationCanceledException){break;}results.Add(new CleanResult{Path=item.OriginalPath,Action=permanent?"彻底删除隔离文件":"恢复隔离文件",Status=ok?"成功":"跳过",Reason=reason,Bytes=item.Length});}after=Free();});
    bool wasCancelled=cancellation.IsCancellationRequested;string log=await Task.Run(()=>WriteLog(results,before,after,wasCancelled));string message="成功 "+results.Count(x=>x.Status=="成功")+" / 跳过 "+results.Count(x=>x.Status!="成功")+(cancellation.IsCancellationRequested?"（任务已取消）":"")+"\r\nC盘可用空间实测变化："+Display.Size(after-before)+"（可能受后台写入影响）。\r\n"+log;
    var skipped=results.Where(x=>x.Status!="成功").Take(8);foreach(var row in skipped)message+="\r\n跳过 "+row.Path+"："+row.Reason;
    status.Text=message;if(!closeRequested)MessageBox.Show(this,message,Text,MessageBoxButtons.OK,MessageBoxIcon.Information);
   }catch(Exception e){status.Text="操作未全部完成："+e.Message;}
   finally {End();}
   if(!closeRequested&&!IsDisposed)await Reload();
  }
  static long Free(){try{return new DriveInfo(@"C:\").AvailableFreeSpace;}catch{return 0;}}
  static string WriteLog(List<CleanResult> results,long before,long after,bool wasCancelled){try {string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"reports");Directory.CreateDirectory(dir);string path=Path.Combine(dir,"quarantine-"+Guid.NewGuid().ToString("N")+".json");ReportWriter.WriteClean(path,new CleanReport{Results=results,FreeBefore=before,FreeAfter=after,Cancelled=wasCancelled});return "操作日志："+path;}catch(Exception e){return "执行日志保存失败："+e.Message;}}
 }
}
