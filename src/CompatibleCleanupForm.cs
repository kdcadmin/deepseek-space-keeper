using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClearDisk {
 internal sealed class CompatibleCleanupForm:Form {
  readonly CompatibleCleanupEngine engine;readonly ListView options=new ListView();readonly TextBox filter=new TextBox(),details=new TextBox();
  readonly Button load=new Button(),preview=new Button(),clean=new Button(),cancel=new Button(),recommended=new Button(),clear=new Button(),elevated=new Button(),scanAll=new Button();
  readonly Label status=new Label(),notice=new Label(),fullSummary=new Label();CompatibleOption[] catalog=new CompatibleOption[0];CompatiblePlan plan;CancellationTokenSource cancellation;bool busy,mutating,updating;
  internal CompatibleCleanupForm():this(new CompatibleCleanupEngine()){}
  internal CompatibleCleanupForm(CompatibleCleanupEngine service):this(service,false){}
  internal CompatibleCleanupForm(CompatibleCleanupEngine service,bool inspectOnOpen){
   engine=service;Text="完整规则扫描与清理 · 系统 / 软件";Name="CompatibleCleanupForm";Font=new Font("Microsoft YaHei UI",9);Size=new Size(1180,860);MinimumSize=new Size(980,730);StartPosition=FormStartPosition.CenterParent;
   var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=1,RowCount=7};Controls.Add(layout);foreach(float height in new[]{85f,52f,50f,34f})layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));layout.RowStyles.Add(new RowStyle(SizeType.Percent,55));layout.RowStyles.Add(new RowStyle(SizeType.Percent,45));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,50));
   notice.Text="可实际清理：BleachBit 5.0.2 的 C 盘适配版 + 原版 Winapp2 251109。包括系统垃圾、软件缓存和可选的使用记录。\r\n规则可能永久删除文件、登录状态或注册表数据；此窗口的清理不进入隔离区。先预览，再确认。\r\n跳过链接、其他盘、个人目录及本程序。回收站请用系统维护的 C 盘操作。占用文件保留并报告失败，不安排重启删除。";notice.Dock=DockStyle.Fill;layout.Controls.Add(notice,0,0);
   fullSummary.Name="fullScanSummary";fullSummary.Dock=DockStyle.Fill;fullSummary.Text="完整规则尚未扫描。这里与主表的安全缓存扫描范围不同。";layout.Controls.Add(fullSummary,0,1);
   var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true};Button[] buttons={scanAll,load,recommended,clear,preview,clean,cancel,elevated};string[] labels={"扫描全部规则","刷新规则目录","选择缓存类","清空选择","预览所选项目","开始实际清理","取消扫描","管理员完整扫描"};for(int i=0;i<buttons.Length;i++){buttons[i].Text=labels[i];buttons[i].AutoSize=true;buttons[i].Height=32;actions.Controls.Add(buttons[i]);}scanAll.Name="scanAllRulesButton";layout.Controls.Add(actions,0,2);
   filter.Dock=DockStyle.Fill;filter.AccessibleName="筛选规则名称或ID";layout.Controls.Add(filter,0,3);
   options.Dock=DockStyle.Fill;options.View=View.Details;options.CheckBoxes=true;options.FullRowSelect=true;options.HideSelection=false;options.Columns.Add("名称 / 分类",340);options.Columns.Add("规则 ID",385);options.Columns.Add("提示",350);layout.Controls.Add(options,0,4);
   details.Dock=DockStyle.Fill;details.Multiline=true;details.ReadOnly=true;details.ScrollBars=ScrollBars.Both;details.WordWrap=false;details.MaxLength=0;details.AccessibleName="规则说明和执行记录";layout.Controls.Add(details,0,5);status.Dock=DockStyle.Fill;status.Text="尚未读取规则；扫描不会自动选择或清理。";layout.Controls.Add(status,0,6);
   scanAll.Click+=async delegate{await InspectAll();};load.Click+=async delegate{await LoadCatalog();};preview.Click+=async delegate{await Preview();};clean.Click+=async delegate{await Clean();};cancel.Click+=delegate{if(cancellation!=null&&!mutating)cancellation.Cancel();};
   filter.TextChanged+=delegate{if(!busy)Fill();};clear.Click+=delegate{foreach(var row in catalog)selected.Remove(row.Id);Fill();InvalidatePreview();};recommended.Click+=delegate{foreach(var row in catalog)if(IsCache(row))selected.Add(row.Id);Fill();InvalidatePreview();};
   options.ItemChecked+=delegate(object sender,ItemCheckedEventArgs e){if(updating)return;var option=e.Item.Tag as CompatibleOption;if(option!=null){if(e.Item.Checked)selected.Add(option.Id);else selected.Remove(option.Id);InvalidatePreview();}};
   options.SelectedIndexChanged+=delegate{if(!busy&&options.SelectedItems.Count==1){var row=(CompatibleOption)options.SelectedItems[0].Tag;details.Text=row.Name+"\r\n"+row.Id+"\r\n"+row.Description+"\r\n"+(String.IsNullOrWhiteSpace(row.Warning)?"请核对预览中的文件/注册表操作；没有专属警告不代表无风险。":row.Warning);}};
   elevated.Click+=delegate{try{Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,"--full-rule-scan-window"){UseShellExecute=true,Verb="runas"});}catch(System.ComponentModel.Win32Exception e){if(e.NativeErrorCode!=1223)MessageBox.Show(this,e.Message,Text);}};
   Shown+=async delegate{if(inspectOnOpen)await InspectAll();};
   FormClosing+=delegate(object sender,FormClosingEventArgs e){if(!busy)return;e.Cancel=true;if(!mutating&&cancellation!=null)cancellation.Cancel();status.Text=mutating?"正在完成本次清理，请等待结果后关闭。":"正在取消预览，结束后可关闭。";};SetBusy(false,false);
  }
  readonly System.Collections.Generic.HashSet<string> selected=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
  internal bool IsWorking {get{return busy;}}internal bool HasPreparedPlan {get{return plan!=null;}}
  static bool IsCache(CompatibleOption row){string id=row.Id;return String.IsNullOrWhiteSpace(row.Warning)&&new[]{"google_chrome.cache","chromium.cache","firefox.cache","brave.cache","opera.cache","thunderbird.cache","system.tmp"}.Contains(id);}
  void Fill(){updating=true;try{options.BeginUpdate();options.Items.Clear();foreach(var row in catalog.Where(o=>String.IsNullOrWhiteSpace(filter.Text)||(o.Name+" "+o.Id).IndexOf(filter.Text,StringComparison.OrdinalIgnoreCase)>=0)){var item=new ListViewItem(new[]{row.Name,row.Id,String.IsNullOrWhiteSpace(row.Warning)?"永久清理；请核对路径/动作":"有专属警告"}){Tag=row,Checked=selected.Contains(row.Id)};options.Items.Add(item);}}finally{options.EndUpdate();updating=false;}}
  void InvalidatePreview(){plan=null;clean.Enabled=false;status.Text="已选 "+selected.Count+" 项；选择改变后请重新预览。";}
  void SetBusy(bool value,bool mutation){busy=value;mutating=mutation;foreach(var control in new Control[]{scanAll,load,recommended,clear,preview,filter,options,elevated})control.Enabled=!value;preview.Enabled=!value&&catalog.Length>0;recommended.Enabled=clear.Enabled=!value&&catalog.Length>0;clean.Enabled=!value&&plan!=null;cancel.Enabled=value&&!mutation;}
  Action<string> Progress(){return message=>{if(IsDisposed||!IsHandleCreated)return;try{BeginInvoke((Action)(()=>{if(!IsDisposed)status.Text=message;}));}catch(InvalidOperationException){}};}
  internal async Task LoadCatalog(){if(busy)return;plan=null;selected.Clear();fullSummary.Text="完整规则尚未扫描；目录数量不是垃圾大小。";SetBusy(true,false);cancellation=new CancellationTokenSource();try{catalog=await Task.Run(()=>engine.List(cancellation.Token));Fill();status.Text="可执行项目 "+catalog.Length+" 项；默认不勾选。可搜索 Windows Update、NVIDIA、Visual Studio 等名称。";}catch(Exception e){catalog=new CompatibleOption[0];Fill();details.Text=e.Message;status.Text="规则目录未读取成功。";}finally{cancellation.Dispose();cancellation=null;SetBusy(false,false);}}
  internal async Task InspectAll(){
   if(busy)return;plan=null;if(catalog.Length==0)await LoadCatalog();if(catalog.Length==0||busy)return;
   SetBusy(true,false);cancellation=new CancellationTokenSource();fullSummary.Text="正在扫描完整系统/软件规则，共 "+catalog.Length+" 项；不会自动选择或清理。";var progress=Progress();
   try{var output=await Task.Run(()=>engine.InspectAll(cancellation.Token,progress));details.Text=output.Output+"\r\nSTDERR:\r\n"+output.Error;
    fullSummary.Text=(output.Success?"完整规则扫描完成":output.Complete?"完整规则扫描部分完成（含诊断/未知项）":"完整规则扫描未完成，大小未知")+(output.Complete?" · 引擎估算 "+output.Size+" · 预计文件 "+output.Files:"")+" · 规则 "+catalog.Length+" · 错误 "+output.Errors+(String.IsNullOrWhiteSpace(output.Error)?"":" · 有诊断提示，见日志")+"\r\n分类可能重叠；估算不是实际释放量。清理需手动勾选并重新预览。";
    status.Text=output.Success?"扫描没有执行清理；勾选需要的项目后点“预览所选项目”。":"存在未知或失败项目，完整范围未确认；请核对日志，必要时使用管理员完整扫描。";
   }catch(OperationCanceledException){fullSummary.Text="完整规则扫描已取消，结果不完整；没有开始清理。";status.Text="可重新扫描。";}catch(Exception e){fullSummary.Text="完整规则扫描失败，大小未知。";details.Text=e.Message;status.Text="没有开始清理。";}finally{plan=null;cancellation.Dispose();cancellation=null;SetBusy(false,false);}
  }
  internal async Task Preview(){if(busy||selected.Count==0)return;plan=null;var ids=selected.ToArray();SetBusy(true,false);cancellation=new CancellationTokenSource();var progress=Progress();try{plan=await Task.Run(()=>engine.Preview(ids,cancellation.Token,progress));details.Text=String.Join("\r\n",catalog.Where(o=>ids.Contains(o.Id)).Select(o=>o.Name+"\r\n"+o.Description+"\r\n"+o.Warning))+"\r\n\r\n--- 引擎操作预览 ---\r\n"+plan.Preview.Output;status.Text="预览完成：引擎估算 "+plan.Preview.Size+"；预计文件 "+plan.Preview.Files+"。选择 "+ids.Length+" 项；有效15分钟。";}catch(OperationCanceledException){status.Text="预览已取消，没有开始清理。";}catch(Exception e){details.Text=e.Message;status.Text="预览不完整，无法开始清理。";}finally{cancellation.Dispose();cancellation=null;SetBusy(false,false);}}
  static long? Available(){try{return new DriveInfo("C").AvailableFreeSpace;}catch(IOException){return null;}}
  async Task Clean(){
   if(busy||plan==null)return;var ids=selected.OrderBy(id=>id,StringComparer.Ordinal).ToArray();string lines=String.Join("\r\n\r\n",catalog.Where(o=>ids.Contains(o.Id)).Select(o=>o.Name+" ["+o.Id+"]\r\n"+o.Description+"\r\n"+o.Warning));
   using(var confirmation=new CompatibleConfirmation(lines,plan.Preview.Size)){if(confirmation.ShowDialog(this)!=DialogResult.OK)return;}
   var prepared=plan;plan=null;SetBusy(true,true);var progress=Progress();long? before=Available();
   try{var output=await Task.Run(()=>engine.Execute(prepared,ids,true,CancellationToken.None,progress));long? after=Available();details.Text=output.Output+"\r\nSTDERR:\r\n"+output.Error;status.Text=(output.Success?"本次执行已完成":"本次存在失败或结果不完整")+"；引擎计数 "+output.Size+"，文件 "+output.Files+"，错误 "+output.Errors+"。\r\n"+(before.HasValue&&after.HasValue?"C盘可用空间观测变化 "+(after.Value-before.Value).ToString("N0")+" 字节（包含其他程序同期变化）。":"C盘可用空间变化未知。");}catch(Exception e){details.Text=e.ToString();status.Text="清理异常；可能已处理部分项目，请核对日志后重新扫描。";}finally{SetBusy(false,false);}
  }
 }
 internal sealed class CompatibleConfirmation:Form {
  internal CompatibleConfirmation(string selected,string size){Text="确认不可撤销清理";Font=new Font("Microsoft YaHei UI",9);Size=new Size(800,600);StartPosition=FormStartPosition.CenterParent;var panel=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),RowCount=4,ColumnCount=1};panel.RowStyles.Add(new RowStyle(SizeType.Absolute,90));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,45));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,42));Controls.Add(panel);panel.Controls.Add(new Label{Dock=DockStyle.Fill,Text="本次引擎估算 "+size+"。执行会重新枚举所选规则，可能永久删除文件、注册表和使用记录。\r\n不会进入隔离区；浏览器登录、历史和应用设置可能受所选规则影响。\r\n关闭相关应用后执行；请核对以下完整项目列表及刚才的路径预览。"},0,0);panel.Controls.Add(new TextBox{Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Both,Text=selected},0,1);var check=new CheckBox{Dock=DockStyle.Fill,Text="我已核对预览和选择，确认执行不可撤销清理"};panel.Controls.Add(check,0,2);var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};var yes=new Button{Text="确认清理",DialogResult=DialogResult.OK,Enabled=false,Width=130};var no=new Button{Text="返回",DialogResult=DialogResult.Cancel,Width=110};actions.Controls.Add(yes);actions.Controls.Add(no);panel.Controls.Add(actions,0,3);check.CheckedChanged+=delegate{yes.Enabled=check.Checked;};AcceptButton=no;CancelButton=no;}
 }
}
