using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClearDisk {
 internal sealed class MigrationDialog:Form {
  readonly IMigrationUiService service;
  readonly Func<string,bool> confirm;
  readonly TextBox source=new TextBox(),destination=new TextBox(),details=new TextBox();
  readonly Button chooseSource=new Button(),chooseDestination=new Button(),prepare=new Button(),execute=new Button(),cancel=new Button(),refresh=new Button(),inspect=new Button(),restore=new Button(),inspectRelease=new Button(),release=new Button();
  readonly Label status=new Label(),summary=new Label();
  readonly ListBox history=new ListBox();
  readonly System.Windows.Forms.Timer pulse=new System.Windows.Forms.Timer();
  readonly object progressGate=new object();
  MigrationPlan plan;
  MigrationRecoveryPreview recoveryPreview;
  MigrationBackupReleasePreview releasePreview;
  CancellationTokenSource cancellation;
  bool busy,closing;
  long generation;
  string latestProgress;
  internal bool HasPreparedPlan {get{return plan!=null;}}
  internal bool IsWorking {get{return busy;}}
  internal MigrationDialog(bool portable):this(portable,new MigrationUiService(),null) {}
  internal MigrationDialog(bool portable,IMigrationUiService backend,Func<string,bool> confirmation) {
   if(backend==null)throw new ArgumentNullException("backend");service=backend;
   confirm=confirmation??(message=>MessageBox.Show(this,message,"确认迁移操作",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)==DialogResult.Yes);
   Text=portable?"软件搬家 · 便携目录":"用户子目录迁移";Font=new Font("Microsoft YaHei UI",9);Size=new Size(1120,820);MinimumSize=new Size(1000,740);StartPosition=FormStartPosition.CenterParent;BackColor=Color.FromArgb(244,250,248);
   var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=8};
   layout.RowStyles.Add(new RowStyle(SizeType.Absolute,68));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,45));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,55));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));Controls.Add(layout);
   var scope=new Label{Dock=DockStyle.Fill,Text=portable?"仅迁移当前用户目录内已关闭且已确认是便携版的软件文件夹。已登记安装目录、AppData、数据库、项目和模型受保护。\r\n整个用户目录、整个桌面/文档等已知文件夹和系统文件不能迁移。":"选择当前用户C盘中的普通子目录，迁至另一块固定NTFS分区。\r\n整个用户目录、整个桌面/文档等已知文件夹和系统文件受保护；请先关闭使用该子目录的应用。",ForeColor=Color.FromArgb(25,91,79)};layout.Controls.Add(scope,0,0);
   AddEndpoint(layout,1,"源目录",source,chooseSource,"migrationChooseSource","选择源目录");
   AddEndpoint(layout,2,"目标新目录",destination,chooseDestination,"migrationChooseDestination","选择目标父目录");
   var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};Setup(prepare,"migrationPrepare","检查并预览");Setup(execute,"migrationExecute","开始迁移");Setup(cancel,"migrationCancel","取消当前任务");Setup(refresh,"migrationRefresh","刷新记录");actions.Controls.AddRange(new Control[]{prepare,execute,cancel,refresh});layout.Controls.Add(actions,0,3);
   summary.Dock=DockStyle.Fill;summary.Text="尚未预检。迁移先保留C盘原件备份，不会立即释放这部分空间。";summary.TextAlign=ContentAlignment.MiddleLeft;layout.Controls.Add(summary,0,4);
   var records=new GroupBox{Text="迁移记录 · 恢复或单独释放备份",Dock=DockStyle.Fill};var recordLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Padding=new Padding(8)};recordLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));recordLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,175));recordLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));recordLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));records.Controls.Add(recordLayout);
   history.Dock=DockStyle.Fill;history.HorizontalScrollbar=true;recordLayout.Controls.Add(history,0,0);recordLayout.SetRowSpan(history,2);
   var recordActions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};Setup(inspect,"migrationInspect","校验所选恢复记录");Setup(restore,"migrationRestore","恢复原目录");Setup(inspectRelease,"migrationInspectRelease","校验备份释放条件");Setup(release,"migrationRelease","永久释放C盘备份");recordActions.Controls.AddRange(new Control[]{inspect,restore,inspectRelease,release});recordLayout.Controls.Add(recordActions,1,0);
   recordLayout.Controls.Add(new Label{Text="记录名称不代表可以恢复",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},1,1);layout.Controls.Add(records,0,5);
   details.Dock=DockStyle.Fill;details.Multiline=true;details.ReadOnly=true;details.ScrollBars=ScrollBars.Both;details.WordWrap=false;details.BackColor=Color.White;layout.Controls.Add(details,0,6);
   status.Name="migrationStatus";status.Dock=DockStyle.Fill;status.Text="尚未开始";status.TextAlign=ContentAlignment.MiddleLeft;layout.Controls.Add(status,0,7);
   chooseSource.Click+=delegate{using(var picker=new FolderBrowserDialog{Description="选择C盘当前用户内的普通子目录",ShowNewFolderButton=false,SelectedPath=source.Text})if(picker.ShowDialog(this)==DialogResult.OK)SelectEndpoints(picker.SelectedPath,destination.Text);};
   chooseDestination.Click+=delegate{using(var picker=new FolderBrowserDialog{Description="选择其他固定NTFS分区的目标父目录；将在里面新建同名目录",ShowNewFolderButton=true})if(picker.ShowDialog(this)==DialogResult.OK){string name=Path.GetFileName(source.Text.TrimEnd('\\'));if(String.IsNullOrWhiteSpace(name)){status.Text="请先选择源目录。";return;}SelectEndpoints(source.Text,Path.Combine(picker.SelectedPath,name));}};
   prepare.Click+=async delegate{await PrepareAsync();};execute.Click+=async delegate{await ExecutePreparedAsync();};cancel.Click+=delegate{CancelWork();};refresh.Click+=async delegate{await RefreshHistoryAsync();};inspect.Click+=async delegate{await PrepareRestoreAsync();};restore.Click+=async delegate{await RestorePreparedAsync();};
   inspectRelease.Click+=async delegate{await PrepareReleaseAsync();};release.Click+=async delegate{await ReleasePreparedAsync();};
   history.SelectedIndexChanged+=delegate{recoveryPreview=null;releasePreview=null;details.Clear();if(!busy)status.Text="所选记录已改变，需要重新校验。";UpdateActions();};
   pulse.Interval=150;pulse.Tick+=delegate{string value;lock(progressGate){value=latestProgress;latestProgress=null;}if(value!=null)status.Text=value;};
   FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy){closing=true;e.Cancel=true;CancelWork();}};FormClosed+=delegate{pulse.Dispose();};UpdateActions();
  }
  static void Setup(Button button,string name,string text) {button.Name=name;button.Text=text;button.Size=new Size(155,33);button.Margin=new Padding(0,0,10,6);}
  static void AddEndpoint(TableLayoutPanel layout,int row,string text,TextBox box,Button choose,string name,string action) {
   var line=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1};line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,105));line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,180));line.Controls.Add(new Label{Text=text,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,0);box.ReadOnly=true;box.Dock=DockStyle.Fill;box.Margin=new Padding(0,6,10,0);line.Controls.Add(box,1,0);Setup(choose,name,action);line.Controls.Add(choose,2,0);layout.Controls.Add(line,0,row);
  }
  internal void SelectEndpoints(string from,string to) {
   if(busy)throw new InvalidOperationException("任务进行中，不能修改迁移路径。");
   source.Text=from??"";destination.Text=to??"";plan=null;recoveryPreview=null;releasePreview=null;details.Clear();summary.Text="路径已改变，需要重新检查并预览。迁移将保留C盘原件备份。";status.Text="尚未预检";UpdateActions();
  }
  void UpdateActions() {
   chooseSource.Enabled=chooseDestination.Enabled=prepare.Enabled=refresh.Enabled=history.Enabled=!busy;
   execute.Enabled=!busy&&plan!=null;inspect.Enabled=inspectRelease.Enabled=!busy&&history.SelectedItem is Guid;restore.Enabled=!busy&&recoveryPreview!=null;release.Enabled=!busy&&releasePreview!=null;cancel.Enabled=busy;
  }
  void BeginWork(string message) {generation++;busy=true;cancellation=new CancellationTokenSource();lock(progressGate)latestProgress=null;status.Text=message;UpdateActions();pulse.Start();}
  void EndWork() {pulse.Stop();lock(progressGate)latestProgress=null;cancellation.Dispose();cancellation=null;busy=false;UpdateActions();if(closing)Close();}
  internal void CancelWork() {if(cancellation!=null){cancellation.Cancel();status.Text="已请求取消，正在等待安全边界…";}}
  IProgress<MigrationProgress> ProgressSink() {long epoch=generation;return new Progress<MigrationProgress>(value=>{if(!busy||IsDisposed||generation!=epoch)return;string text=ProgressOperation(value)+" · "+value.Files.ToString("N0")+" / "+value.TotalFiles.ToString("N0")+" 个文件 · "+Display.Size(value.Bytes)+" / "+Display.Size(value.TotalBytes);lock(progressGate)latestProgress=text;});}
  static string ProgressOperation(MigrationProgress value) {if(value.Operation=="CopyingAndVerifying")return "复制并校验";if(value.Operation=="Restoring")return "恢复原目录";if(value.Operation=="RevalidatingBackupRelease")return "再次核验备份释放";if(value.Operation=="DeletingTrackedBackupFile")return "删除已核验备份文件";if(value.Operation=="DeletingTrackedEmptyDirectory")return "移除已核验空目录";switch(value.Stage){case MigrationStage.Prepared:return "预检完成";case MigrationStage.SourceRenameIntent:return "准备保留原件";case MigrationStage.SourceBackedUp:return "原件备份完成";case MigrationStage.CopyIntent:return "复制并校验";case MigrationStage.CopyVerified:return "副本校验完成";case MigrationStage.PublishIntent:return "准备发布目标目录";case MigrationStage.TargetPublished:return "目标目录已发布";case MigrationStage.JunctionIntent:return "准备切换原路径";case MigrationStage.CommittedBackupRetained:return "原路径已切换，备份保留";default:return "正在处理";}}
  internal async Task PrepareAsync() {
   if(busy)return;plan=null;recoveryPreview=null;releasePreview=null;details.Clear();summary.Text="正在重新预检；尚未取得本次迁移执行资格。";string from=source.Text,to=destination.Text;BeginWork("正在检查目录、空间和完整文件内容…");
   try{var prepared=await Task.Run(()=>service.Prepare(from,to,cancellation.Token));cancellation.Token.ThrowIfCancellationRequested();plan=prepared;summary.Text="预检通过 · "+prepared.FileCount.ToString("N0")+" 个文件 · "+prepared.DirectoryCount.ToString("N0")+" 个目录 · "+Display.Size(prepared.Bytes);details.Text="源："+prepared.Source+"\r\n目标："+prepared.Destination+"\r\n执行时还会重新校验。提交后C盘原件备份保留，空间尚未释放。";status.Text="预检完成，请核对路径后开始迁移。";}
   catch(OperationCanceledException){summary.Text="预检已取消，需要重新检查。";status.Text="预检已取消，未取得迁移执行资格。";}
   catch(Exception ex){summary.Text="预检未通过，不能开始迁移。";status.Text="预检未通过："+ex.Message;}
   finally{EndWork();}
  }
  internal async Task ExecutePreparedAsync() {
   if(busy||plan==null)return;MigrationPlan selected=plan;
   if(!confirm("将迁移以下目录并在原路径创建链接：\r\n\r\n源："+selected.Source+"\r\n目标："+selected.Destination+"\r\n文件："+selected.FileCount.ToString("N0")+"，逻辑大小："+Display.Size(selected.Bytes)+"\r\n\r\nC盘原件会保留为备份，此时尚未释放这部分空间。是否开始？"))return;
   if(!Object.ReferenceEquals(plan,selected))return;
   plan=null;recoveryPreview=null;releasePreview=null;summary.Text="迁移处理中，请等待执行结果。";BeginWork("正在重新校验并执行迁移…");var progress=ProgressSink();
   try{var result=await Task.Run(()=>service.Execute(selected,cancellation.Token,progress));ShowResult(result);}
   catch(OperationCanceledException){summary.Text="任务已取消，请重新核对当前数据状态。";status.Text="任务已取消，请刷新恢复记录核对当前状态。";}
   catch(Exception ex){summary.Text="迁移未完成，请重新预检并核对恢复记录。";status.Text="迁移未完成："+ex.Message;details.AppendText("\r\n请刷新恢复记录；在核对状态前保留源、目标和备份。");}
   finally{EndWork();}
  }
  internal async Task RefreshHistoryAsync() {
   if(busy)return;recoveryPreview=null;releasePreview=null;history.Items.Clear();details.Clear();BeginWork("正在读取迁移记录列表…");
   try{var items=await Task.Run(()=>service.ListTransactions(cancellation.Token));cancellation.Token.ThrowIfCancellationRequested();foreach(Guid item in items)history.Items.Add(item);status.Text="找到 "+items.Count+" 条候选记录；选择后需完整校验。";}
   catch(OperationCanceledException){status.Text="读取记录已取消。";}
   catch(Exception ex){status.Text="无法完整读取记录："+ex.Message;}
   finally{EndWork();}
  }
  internal async Task PrepareRestoreAsync() {
   if(busy||!(history.SelectedItem is Guid))return;Guid selected=(Guid)history.SelectedItem;recoveryPreview=null;releasePreview=null;details.Clear();BeginWork("正在校验记录、卷身份和所有现有文件…");
   try{var preview=await Task.Run(()=>service.InspectRecovery(selected,cancellation.Token));cancellation.Token.ThrowIfCancellationRequested();recoveryPreview=preview;details.Text="事务："+preview.Transaction+"\r\n源："+preview.Source+"\r\n目标："+preview.Destination+"\r\n备份："+preview.Backup+"\r\n"+preview.Message;status.Text="恢复预检通过；执行前仍会再次校验。";}
   catch(OperationCanceledException){status.Text="恢复预检已取消。";}
   catch(Exception ex){status.Text="此记录暂不能恢复："+ex.Message;}
   finally{EndWork();}
  }
  internal async Task RestorePreparedAsync() {
   if(busy||recoveryPreview==null)return;var selected=recoveryPreview;
   if(!confirm("恢复以下原目录：\r\n\r\n源："+selected.Source+"\r\n备份："+selected.Backup+"\r\n目标副本："+selected.Destination+"\r\n文件："+selected.Files.ToString("N0")+"，逻辑大小："+Display.Size(selected.Bytes)+"\r\n\r\n只在现有数据仍与校验基线一致时恢复；遇到冲突会保留数据。是否恢复？"))return;
   if(!Object.ReferenceEquals(recoveryPreview,selected))return;
   recoveryPreview=null;releasePreview=null;plan=null;summary.Text="恢复处理中，请等待执行结果。";BeginWork("正在重新校验并恢复原目录…");var progress=ProgressSink();
   try{var result=await Task.Run(()=>service.Restore(selected,cancellation.Token,progress));ShowResult(result);}
   catch(OperationCanceledException){summary.Text="恢复已取消，请重新校验记录。";status.Text="恢复已取消，请重新读取并校验记录。";}
   catch(Exception ex){summary.Text="恢复未完成，请核对源、目标和备份。";status.Text="恢复未完成："+ex.Message;}
   finally{EndWork();}
  }
  internal async Task PrepareReleaseAsync() {
   if(busy||!(history.SelectedItem is Guid))return;Guid selected=(Guid)history.SelectedItem;
   releasePreview=null;recoveryPreview=null;plan=null;details.Clear();BeginWork("正在重新校验目标副本、原路径链接和C盘备份…");
   try{var preview=await Task.Run(()=>service.InspectRelease(selected,cancellation.Token));cancellation.Token.ThrowIfCancellationRequested();releasePreview=preview;details.Text="事务："+preview.Transaction+"\r\n原路径："+preview.Source+"\r\n使用中的目标："+preview.Destination+"\r\n待永久删除的备份："+preview.Backup+"\r\n现存："+preview.Files.ToString("N0")+" 个文件，"+preview.Directories.ToString("N0")+" 个目录，"+preview.Bytes.ToString("N0")+" 逻辑字节\r\n此前缺失："+preview.PreviouslyMissingFiles.ToString("N0")+" 个文件，"+preview.PreviouslyMissingBytes.ToString("N0")+" 字节，"+preview.PreviouslyMissingDirectories.ToString("N0")+" 个目录；不计作本次释放。\r\n一旦开始永久删除，旧的完整恢复点即失效，取消也不能撤销已删除内容。";status.Text="备份释放预检通过；请核对后另行确认永久删除。";}
   catch(OperationCanceledException){status.Text="备份释放预检已取消。";}
   catch(Exception ex){status.Text="暂不能释放此备份："+ex.Message;}
   finally{EndWork();}
  }
  internal async Task ReleasePreparedAsync() {
   if(busy||releasePreview==null)return;var selected=releasePreview;
   if(!confirm("将永久删除以下C盘备份，不可恢复：\r\n\r\n备份："+selected.Backup+"\r\n原路径："+selected.Source+"\r\n使用中的目标："+selected.Destination+"\r\n现存："+selected.Files+" 个文件，"+selected.Directories+" 个目录，"+selected.Bytes+" 逻辑字节（"+Display.Size(selected.Bytes)+"）\r\n此前已缺失："+selected.PreviouslyMissingFiles+" 个文件，"+selected.PreviouslyMissingBytes+" 字节，"+selected.PreviouslyMissingDirectories+" 个目录，不计为本次释放。\r\n\r\n开始后旧的完整恢复点失效；中途取消也不能恢复已删除的备份内容。原路径链接和目标副本继续使用。是否永久释放？"))return;
   if(!Object.ReferenceEquals(releasePreview,selected))return;
   releasePreview=null;recoveryPreview=null;plan=null;summary.Text="正在释放备份，请等待确认结果。";BeginWork("正在重新核验并释放所选备份…");var progress=ProgressSink();
   try{var result=await Task.Run(()=>service.ReleaseBackup(selected,cancellation.Token,progress));ShowReleaseResult(result);}
   catch(OperationCanceledException){summary.Text="释放已取消，请重新校验备份；已完成的删除无法撤销。";status.Text="已取消，请刷新并重新校验记录。";}
   catch(Exception ex){summary.Text="释放未完成，请重新校验备份；请保留目标副本和原路径链接。";status.Text="释放停止："+ex.Message;}
   finally{EndWork();}
  }
  void ShowReleaseResult(MigrationBackupReleaseResult result) {
   status.Text=result.Completed?(result.CompletionRecorded?"C盘备份已确认删除，完成记录已保存。":"C盘备份已确认删除，但完成记录保存失败。"):(result.Cancelled?"释放已取消，请重新校验剩余备份。":"释放未完成，请重新校验剩余备份。");
   summary.Text=result.ReleaseIntentPersisted?"旧的完整恢复点已失效；原路径链接和目标副本继续使用。":"未确认开始释放，请按结果重新校验；目标副本保留。";
   string before=result.FreeBytesBefore.HasValue?result.FreeBytesBefore.Value.ToString("N0")+" 字节":"无法确认";string after=result.FreeBytesAfter.HasValue?result.FreeBytesAfter.Value.ToString("N0")+" 字节":"无法确认";
   string space=result.FreeBytesDelta.HasValue?result.FreeBytesDelta.Value.ToString("N0")+" 字节（整卷同期变化，不等于本次删除量）":"无法确认（未取得完整前后观测）";
   details.Text="事务："+result.Transaction+"\r\n备份："+result.Backup+"\r\n目标："+result.Destination+"\r\n本次确认删除："+result.DeletedFiles+" 个文件，"+result.DeletedDirectories+" 个目录；"+Display.Size(result.DeletedBytes)+" / "+result.DeletedBytes.ToString("N0")+" 逻辑字节\r\n删除请求：文件 "+result.FileDeletionRequests+"，目录 "+result.DirectoryDeletionRequests+(result.UnconfirmedNativeEffects?"；部分原生操作效果尚无法确认":"")+"\r\n此前缺失："+result.PreviouslyMissingFiles+" 个文件、"+result.PreviouslyMissingBytes+" 字节、"+result.PreviouslyMissingDirectories+" 个目录，不计入本次删除。\r\n剩余记录："+result.RemainingFiles+" 个文件，"+result.RemainingDirectories+" 个目录，"+result.RemainingBytes.ToString("N0")+" 逻辑字节"+(result.RemainingInventoryVerified?"（当前清单已核验）":"（预览减去确认删除量，当前成员尚未重新核验）")+"\r\n未知成员："+(result.Unknown<0?"无法确认":result.Unknown.ToString())+"；跳过记录："+result.Skipped+"\r\nC盘可用空间：之前 "+before+"；之后 "+after+"\r\nC盘可用空间前后差："+space+"\r\n"+result.Message;
  }
  static string ResultTitle(MigrationExecutionResult result) {switch(result.Status){case MigrationExecutionStatus.CommittedBackupRetained:return "迁移已完成，C盘原件备份保留。";case MigrationExecutionStatus.RestoredSource:return "原目录已恢复，目标副本保留。";case MigrationExecutionStatus.CancelledSourceRestored:return "迁移已取消，源目录已恢复；暂存或目标副本保留。";case MigrationExecutionStatus.FailedSourceRestored:return "迁移未完成，源目录已恢复；暂存或目标副本保留。";case MigrationExecutionStatus.PreservedConflict:return "检测到冲突，数据已保留；请校验恢复记录。";default:return result.Cancelled?"迁移已取消，源目录未移动。":"迁移未开始，源目录未移动。";}}
  void ShowResult(MigrationExecutionResult result) {status.Text=ResultTitle(result);details.Text="事务："+result.Transaction+"\r\n状态："+ResultTitle(result)+"\r\n源："+result.Source+"\r\n目标："+result.Destination+"\r\n备份："+result.Backup+"\r\n暂存："+result.Staging+"\r\n"+result.Message;summary.Text=result.Status==MigrationExecutionStatus.CommittedBackupRetained?"迁移入口已切换；C盘备份保留，尚未释放这部分空间。":"请按结果核对源、目标和备份；未确认前保留所有副本。";}
 }
}
