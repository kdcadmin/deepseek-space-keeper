using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClearDisk {
 public sealed partial class MainForm : Form {
  readonly SafetyPolicy policy;
  readonly HashSet<FileRecord> selected=new HashSet<FileRecord>();
  ScanReport report;
  List<FileRecord> visible=new List<FileRecord>();
  readonly DataGridView files=new DataGridView(), folders=new DataGridView();
  readonly ListBox warnings=new ListBox();
  readonly TextBox search=new TextBox();
  readonly Label drive=new Label(), state=new Label(), counts=new Label(), selection=new Label(), detail=new Label();
  readonly TabControl tabs=new TabControl();
  readonly CheckBox inspect=new CheckBox();
  readonly Button caches=new Button(), disk=new Button(), choose=new Button(), duplicates=new Button(), cancel=new Button(), all=new Button(), clear=new Button(), preview=new Button(), export=new Button(), open=new Button();
  Button headerCancel;
  readonly System.Windows.Forms.Timer debounce=new System.Windows.Forms.Timer(), pulse=new System.Windows.Forms.Timer();
  readonly ProgressBar activity=new ProgressBar();
  readonly DriveUsageBar meter=new DriveUsageBar();
  readonly Label emptyFiles=new Label();
  readonly object progressLock=new object();
  ScanProgress latest;
  CancellationTokenSource cancellation;
  bool busy,closeRequested,allowExit,askingExit,ascending,canCancel=true;
  readonly Color ink=Color.FromArgb(23,58,143), blue=Color.FromArgb(31,79,184);
  internal static bool EnableDesktopPet;
  static readonly uint wakeMessage=RegisterWindowMessage("DaFeiYu.SpaceKeeper.Show");
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
  DesktopPetForm pet;
  internal int SelectedCount { get { return selected.Count; } }
  internal int VisibleCount { get { return visible.Count; } }
  internal string StateText { get { return state.Text; } }
  internal bool IsBusy { get { return busy; } }
  public MainForm():this(SafetyPolicy.ForCurrentUser()) { }
  internal MainForm(SafetyPolicy safety) {
   policy=safety;
   Text="大肥鱼空间管家"; Name="MainForm"; Font=new Font("Microsoft YaHei UI",9F); BackColor=Color.White; ForeColor=ink;
   Size=new Size(1280,900); MinimumSize=new Size(1100,800); StartPosition=FormStartPosition.CenterScreen; AutoScaleMode=AutoScaleMode.Dpi;
   var layout=mainLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=8,Padding=new Padding(20,14,20,12)};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
   foreach(var h in new[]{108F,148F,42F}) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
   layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
   foreach(var h in new[]{36F,60F,48F,27F}) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
   Controls.Add(layout);
   var header=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=3,BackColor=Color.White,Padding=new Padding(4,0,0,2),Margin=new Padding(0,0,0,6)};
   header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,58)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,42));
   header.RowStyles.Add(new RowStyle(SizeType.Absolute,34)); header.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); header.RowStyles.Add(new RowStyle(SizeType.Absolute,16));
   var title=new Label {Text="大肥鱼空间管家",Font=new Font(Font.FontFamily,16,FontStyle.Bold),ForeColor=ink,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
   drive.ForeColor=ink;drive.Dock=DockStyle.Fill;drive.TextAlign=ContentAlignment.MiddleRight;drive.Font=new Font(Font.FontFamily,10,FontStyle.Bold);drive.AutoEllipsis=true;drive.Margin=Padding.Empty;
   var subtitle=new Label {Text="先看占用，再预览，确认后才清理。",ForeColor=Color.FromArgb(96,112,106),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
   var headerTools=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,BackColor=Color.White,Margin=Padding.Empty,Padding=new Padding(0,2,0,0)};
   var backHome=new Button();Configure(backHome,"返回首页",96,false);backHome.Name="homeBackButton";backHome.Size=new Size(96,28);backHome.Margin=new Padding(8,0,0,0);backHome.Click+=delegate{ShowHome();};
   headerCancel=new Button();Configure(headerCancel,"取消",68,false);headerCancel.Size=new Size(68,28);headerCancel.Margin=new Padding(8,0,0,0);headerCancel.Enabled=false;headerCancel.Click+=delegate{CancelOperation();};
   headerTools.Controls.Add(headerCancel);headerTools.Controls.Add(backHome);
   header.Controls.Add(title,0,0);header.Controls.Add(drive,1,0);header.Controls.Add(subtitle,0,1);header.Controls.Add(headerTools,1,1);header.SetColumnSpan(meter,2);header.Controls.Add(meter,0,2);layout.Controls.Add(header,0,0);
   var actions=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true,AutoScroll=true,Padding=new Padding(0,5,0,0)};
   caches.Name="fullRuleScanButton";Configure(caches,"完整规则扫描",176,true);caches.Font=new Font(Font,FontStyle.Bold);Configure(disk,"C盘占用",107,false);Configure(choose,"选择目录分析",140,false);Configure(duplicates,"重复文件检查",140,false);Configure(cancel,"取消",78,false);
   actions.Controls.AddRange(new Control[]{caches,disk,choose,duplicates,cancel});
   inspect.Text="安全扫描含开发占用（只读）";inspect.AutoSize=true;inspect.Margin=new Padding(4,11,9,0);inspect.Checked=true;actions.Controls.Add(inspect);InitializeExtendedActions(actions);layout.Controls.Add(actions,0,1);
   var filter=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1};filter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,85));filter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));filter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,210));
   filter.Controls.Add(new Label {Text="筛选结果",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,0);
   search.Dock=DockStyle.Fill;search.Margin=new Padding(0,6,12,5);search.AccessibleName="搜索分类、原因或路径";filter.Controls.Add(search,1,0);
   counts.Dock=DockStyle.Fill;counts.TextAlign=ContentAlignment.MiddleRight;filter.Controls.Add(counts,2,0);InitializeExtraFilters(filter);layout.Controls.Add(filter,0,2);
   tabs.Dock=DockStyle.Fill;tabs.Margin=Padding.Empty;
   TabPage fileTab=new TabPage("文件明细"),folderTab=new TabPage("文件夹占用"),warningTab=new TabPage("提示与跳过项");tabs.TabPages.AddRange(new[]{fileTab,folderTab,warningTab});
   SetupGrid(files);files.VirtualMode=true;files.ReadOnly=false;
   files.Columns.Add(new DataGridViewCheckBoxColumn {Name="Selected",HeaderText="选择",Width=54,ReadOnly=false,SortMode=DataGridViewColumnSortMode.NotSortable});
   AddColumn(files,"Size","逻辑大小 ↓",105);AddColumn(files,"Category","分类 / 分组",180);AddColumn(files,"Risk","处理方式",120);AddColumn(files,"Reason","原因",300);AddColumn(files,"Path","完整路径",460);AddColumn(files,"RuleId","规则 ID",190);
   files.Columns["Size"].SortMode=DataGridViewColumnSortMode.Programmatic;
   files.CellValueNeeded+=delegate(object s,DataGridViewCellValueEventArgs e) { if(e.RowIndex>=visible.Count)return;var f=visible[e.RowIndex];switch(e.ColumnIndex) {case 0:e.Value=selected.Contains(f);break;case 1:e.Value=Display.Size(f.Length);break;case 2:e.Value=(f.IsKeeper?"【保留项】 ":"")+(f.Category??"")+(String.IsNullOrEmpty(f.DuplicateGroup)?"":" / "+f.DuplicateGroup);break;case 3:e.Value=Eligible(f)?"可清理缓存":(Organizable(f)?"可手动隔离":(f.Risk==RiskLevel.Protected?"受保护":"仅检查 / 打开"));break;case 4:e.Value=f.Reason;break;case 5:e.Value=f.Path;break;case 6:e.Value=f.RuleId;break;} };
   files.CellValuePushed+=delegate(object s,DataGridViewCellValueEventArgs e) {if(e.ColumnIndex==0 && e.RowIndex<visible.Count) {var f=visible[e.RowIndex];if(!busy&&Selectable(f)&&Convert.ToBoolean(e.Value))selected.Add(f);else selected.Remove(f);UpdateSelection();}};
   files.CellBeginEdit+=delegate(object s,DataGridViewCellCancelEventArgs e) { e.Cancel=busy||e.ColumnIndex!=0||!Selectable(visible[e.RowIndex]); };
   files.CellPainting+=delegate(object s,DataGridViewCellPaintingEventArgs e) {if(e.RowIndex>=0&&e.RowIndex<visible.Count&&e.ColumnIndex==0&&!Selectable(visible[e.RowIndex])){e.Paint(e.ClipBounds,DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);Size box=CheckBoxRenderer.GetGlyphSize(e.Graphics,System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled);CheckBoxRenderer.DrawCheckBox(e.Graphics,new Point(e.CellBounds.Left+(e.CellBounds.Width-box.Width)/2,e.CellBounds.Top+(e.CellBounds.Height-box.Height)/2),System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled);e.Handled=true;}}; files.CellToolTipTextNeeded+=delegate(object s,DataGridViewCellToolTipTextNeededEventArgs e){if(e.RowIndex>=0&&e.RowIndex<visible.Count)e.ToolTipText=visible[e.RowIndex].Path+"\r\n"+visible[e.RowIndex].Reason;}; files.CurrentCellDirtyStateChanged+=delegate {if(files.IsCurrentCellDirty)files.CommitEdit(DataGridViewDataErrorContexts.Commit);};
   files.CellFormatting+=delegate(object s,DataGridViewCellFormattingEventArgs e) {if(e.RowIndex>=0&&e.RowIndex<visible.Count&&!Selectable(visible[e.RowIndex]))e.CellStyle.ForeColor=Color.FromArgb(110,120,134);};
   files.ColumnHeaderMouseClick+=delegate(object s,DataGridViewCellMouseEventArgs e) {if(e.ColumnIndex==1){ascending=!ascending;files.Columns[1].HeaderText=ascending?"逻辑大小 ↑":"逻辑大小 ↓";ApplyFilter();}};
   files.CellDoubleClick+=delegate(object s,DataGridViewCellEventArgs e) {if(e.RowIndex>=0&&e.ColumnIndex!=0)OpenLocation();};
   fileTab.Controls.Add(files);
   emptyFiles.Font=new Font(Font.FontFamily,12F);emptyFiles.ForeColor=Color.FromArgb(91,107,127);files.Paint+=delegate(object s,PaintEventArgs e){if(files.RowCount>0)return;var area=files.ClientRectangle;area.Y+=files.ColumnHeadersHeight;area.Height-=files.ColumnHeadersHeight;if(area.Height<20)return;TextRenderer.DrawText(e.Graphics,"还没有扫描结果\r\n返回首页，从 C盘清理、垃圾清理或 AI 缓存进入。",emptyFiles.Font,area,emptyFiles.ForeColor,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak);};
   var fileMenu=new ContextMenuStrip();var copyPath=new ToolStripMenuItem("复制路径");var openHere=new ToolStripMenuItem("打开文件位置");copyPath.Click+=delegate{CopyCurrentPath();};openHere.Click+=delegate{OpenLocation();};fileMenu.Items.Add(copyPath);fileMenu.Items.Add(openHere);files.ContextMenuStrip=fileMenu;
   SetupGrid(folders);AddColumn(folders,"Bytes","逻辑大小",130);AddColumn(folders,"Count","文件数",110);AddColumn(folders,"Path","文件夹（扫描时占用，父子目录数值重叠，请勿相加）",850);folderTab.Controls.Add(folders);
   folders.CellDoubleClick+=delegate(object s,DataGridViewCellEventArgs e) {if(e.RowIndex>=0)LaunchLocation(Convert.ToString(folders.Rows[e.RowIndex].Cells[2].Value),true);};
   warnings.Dock=DockStyle.Fill;warnings.HorizontalScrollbar=true;warnings.BorderStyle=BorderStyle.None;warnings.ItemHeight=25;warningTab.Controls.Add(warnings);layout.Controls.Add(tabs,0,3);
   detail.Dock=DockStyle.Fill;detail.AutoEllipsis=true;detail.TextAlign=ContentAlignment.MiddleLeft;detail.ForeColor=Color.FromArgb(91,107,127);detail.Text="点击“完整规则扫描”，在本界面查看、全选、导出和清理；安全缓存扫描为单独的小范围入口。";layout.Controls.Add(detail,0,4);
   var tools=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true};Configure(all,"全选可清理缓存",155,false);Configure(clear,"清空选择",108,false);Configure(open,"打开文件位置",140,false);Configure(export,"导出报告",110,false);Configure(preview,"预览并清理…",145,true);tools.Controls.AddRange(new Control[]{all,clear,open,export,preview});InitializeOrganizationTools(tools);layout.Controls.Add(tools,0,5);
   var bottom=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2};bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,55));bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,45));bottom.RowStyles.Add(new RowStyle(SizeType.Percent,100));bottom.RowStyles.Add(new RowStyle(SizeType.Absolute,14));selection.Dock=DockStyle.Fill;selection.TextAlign=ContentAlignment.MiddleLeft;selection.Font=new Font(Font,FontStyle.Bold);state.Dock=DockStyle.Fill;state.TextAlign=ContentAlignment.MiddleRight;activity.Dock=DockStyle.Fill;activity.Style=ProgressBarStyle.Marquee;activity.MarqueeAnimationSpeed=0;activity.Visible=false;activity.Margin=new Padding(0,2,0,0);bottom.Controls.Add(selection,0,0);bottom.Controls.Add(state,1,0);bottom.SetColumnSpan(activity,2);bottom.Controls.Add(activity,0,1);layout.Controls.Add(bottom,0,6);
   layout.Controls.Add(new Label {Text="确认之后才会删除。清掉的缓存下次使用时会重新生成。",Dock=DockStyle.Fill,ForeColor=Color.FromArgb(120,128,122),TextAlign=ContentAlignment.MiddleLeft},0,7);
   debounce.Interval=220;debounce.Tick+=delegate {debounce.Stop();ApplyFilter();};search.TextChanged+=delegate {debounce.Stop();debounce.Start();};
   pulse.Interval=180;pulse.Tick+=delegate {ScanProgress p;lock(progressLock){p=latest;latest=null;}if(p!=null){state.Text="已检查 "+p.Files.ToString("N0")+" 个文件 · "+Display.Size(p.Bytes);detail.Text=p.Message;}};
   all.Click+=delegate {SelectEligible();};clear.Click+=delegate {selected.Clear();files.Invalidate();UpdateSelection();};open.Click+=delegate {OpenLocation();};export.Click+=async delegate {await ExportReport();};preview.Click+=async delegate {await PreviewAndClean();};
   caches.Click+=async delegate {await OpenCompatibleCleanup(true);};
   disk.Click+=async delegate {await RunScan("C盘占用分析",delegate(CancellationToken t,Action<ScanProgress> p){return new Scanner(policy).ScanDisk(@"C:\",t,p);});};
   choose.Click+=async delegate {string root=PickFolder("选择要分析的文件夹（扫描不修改文件）");if(root!=null)await RunScan("目录占用分析",delegate(CancellationToken t,Action<ScanProgress> p){return new Scanner(policy).ScanDisk(root,t,p);});};
   duplicates.Click+=async delegate {string root=PickFolder("选择普通个人文件目录进行内容比对（保留项锁定，默认不选）");if(root!=null)await RunScan("重复文件检查",delegate(CancellationToken t,Action<ScanProgress> p){return new DuplicateFinder(policy).Scan(root,t,p);});};
   cancel.Click+=delegate {CancelOperation();};FormClosing+=OnClosing;FormClosed+=delegate {debounce.Dispose();pulse.Dispose();};
   layout.RowStyles[1].Height=0;
   InitializeNavigation(layout);
   try{Icon=FishPainter.Icon;}catch(Exception){}
   Shown+=delegate{if(!EnableDesktopPet||pet!=null)return;pet=new DesktopPetForm(this);pet.Show();pet.Attach();};
   KeyPreview=true;KeyDown+=delegate(object s,KeyEventArgs e){if(e.KeyCode==Keys.Escape&&busy){CancelOperation();e.Handled=true;}else if(e.Control&&e.KeyCode==Keys.F){search.Focus();search.SelectAll();e.Handled=true;}else if(e.Control&&e.KeyCode==Keys.C&&files.ContainsFocus){CopyCurrentPath();e.Handled=true;}};UpdateDrive();UpdateSelection();SetBusy(false);state.Text="就绪 · 尚未扫描";
  }
  void Configure(Button b,string text,int width,bool primary) {b.Text=text;b.Size=new Size(width,33);b.Margin=new Padding(0,3,9,3);b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=primary?blue:Color.FromArgb(191,219,254);b.BackColor=primary?blue:Color.White;b.ForeColor=primary?Color.White:ink;b.Cursor=Cursors.Hand;}
  static void SetupGrid(DataGridView grid) {grid.Dock=DockStyle.Fill;grid.BorderStyle=BorderStyle.None;grid.BackgroundColor=Color.White;grid.RowHeadersVisible=false;grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;grid.AllowUserToResizeRows=false;grid.ReadOnly=true;grid.MultiSelect=false;grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;grid.AutoGenerateColumns=false;grid.RowTemplate.Height=30;grid.ColumnHeadersHeight=34;grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing;grid.EnableHeadersVisualStyles=false;grid.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(239,246,255);grid.ColumnHeadersDefaultCellStyle.ForeColor=Color.FromArgb(27,58,110);grid.ColumnHeadersDefaultCellStyle.Font=new Font("Microsoft YaHei UI",9F);grid.DefaultCellStyle.BackColor=Color.White;grid.DefaultCellStyle.SelectionBackColor=Color.FromArgb(219,234,254);grid.DefaultCellStyle.SelectionForeColor=Color.FromArgb(27,58,110);grid.AlternatingRowsDefaultCellStyle.BackColor=Color.FromArgb(248,251,255);grid.GridColor=Color.FromArgb(219,234,254);grid.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;}
  static void AddColumn(DataGridView grid,string name,string text,int width) {grid.Columns.Add(new DataGridViewTextBoxColumn {Name=name,HeaderText=text,Width=width,ReadOnly=true,SortMode=DataGridViewColumnSortMode.NotSortable});}
  bool Eligible(FileRecord f) {return report!=null&&report.Mode=="cache"&&f.CanDelete&&f.Risk==RiskLevel.Safe&&!f.IsKeeper&&String.IsNullOrEmpty(f.DuplicateGroup);}
  internal void LoadReport(ScanReport value) {
   HideCleanupWorkspace();report=value;selected.Clear();search.Text="";ResetExtraFilters();
   if(report.Mode!="cache")foreach(var f in report.Files){f.CanDelete=false;f.CanRecycle=false;}
   PopulateSummary();folders.Rows.Clear();foreach(var f in report.Folders.OrderByDescending(x=>x.Bytes))folders.Rows.Add(Display.Size(f.Bytes),f.Files.ToString("N0"),f.Path);
   warnings.Items.Clear();foreach(string w in report.Warnings)warnings.Items.Add(w);if(warnings.Items.Count==0)warnings.Items.Add("没有额外提示。无法访问、受保护或链接项目会被跳过。");
   ApplyFilter();tabs.TabPages[1].Text="文件夹占用 ("+report.Folders.Count.ToString("N0")+")";tabs.TabPages[2].Text="提示与跳过项 ("+report.Warnings.Count+")";
   state.Text=report.Cancelled?"已取消 · 当前结果不完整":report.Incomplete?"检查已结束 · 存在未知 / 未检查项":(report.Files.Count==0&&report.Folders.Count==0?"扫描完成 · 没有可显示项目":"扫描完成 · "+report.Seconds.ToString("0.0")+" 秒");
   detail.Text=(report.Mode=="cache"?"安全缓存扫描（小范围）：":"已遍历 ")+report.ScannedFiles.ToString("N0")+" 个文件 · "+Display.Size(report.LogicalBytes)+" 逻辑大小 · 跳过 "+report.Skipped.ToString("N0")+" 项"+(report.Mode=="cache"?"；直接可清理 "+Display.Size(report.Files.Where(x=>x.CanDelete).Sum(x=>x.Length))+"。完整系统/软件规则另行扫描。":report.Mode=="reference-inventory"?"；全局按文件身份去重。仅作原版规则对照，本结果不允许清理。":"；扫描不会修改文件，合格个人文件可切换手动选择后隔离。");
   PopulateReferenceResults();UpdateSelection();
  }
  internal void ApplyFilter() {string q=search.Text.Trim();visible=report==null?new List<FileRecord>():report.Files.Where(f=>ExtraFilter(f)&&(q.Length==0||((f.Category??"")+" "+(f.Reason??"")+" "+f.Path+" "+f.DuplicateGroup).IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0)).ToList();visible=ascending?visible.OrderBy(f=>f.Length).ToList():visible.OrderByDescending(f=>f.Length).ToList();files.RowCount=visible.Count;files.Invalidate();counts.Text="显示 "+visible.Count.ToString("N0")+" / "+(report==null?0:report.Files.Count).ToString("N0")+" 个文件";}
  internal void SetSearch(string value) {search.Text=value;debounce.Stop();ApplyFilter();}
  internal void SelectEligible() {if(busy||report==null||organizationMode.Checked)return;foreach(var f in visible)if(Eligible(f))selected.Add(f);files.Invalidate();UpdateSelection();}
  void UpdateSelection() {selection.Text="已选 "+selected.Count.ToString("N0")+(organizationMode.Checked?" 个个人文件":" 个缓存文件")+" · "+Display.Size(selected.Sum(x=>x.Length))+"（逻辑大小）";preview.Enabled=!busy&&selected.Count>0;all.Enabled=!busy&&report!=null&&report.Mode=="cache";clear.Enabled=!busy&&selected.Count>0;export.Enabled=!busy&&report!=null;open.Enabled=!busy&&visible.Count>0;UpdateExtendedSelection();}
  void UpdateDrive() {try {var d=new DriveInfo(@"C:\");long used=d.TotalSize-d.AvailableFreeSpace;drive.Text="C:  可用 "+Display.Size(d.AvailableFreeSpace)+"  /  已用 "+Display.Size(used);meter.Ratio=d.TotalSize<=0?0:(double)used/d.TotalSize;}catch{drive.Text="C:  暂时无法读取磁盘空间";meter.Ratio=0;}if(home!=null){home.SpaceText=drive.Text;home.UsedRatio=meter.Ratio;home.Invalidate();}}
  void SetBusy(bool value) {busy=value;foreach(var b in new[]{caches,disk,choose,duplicates})b.Enabled=!value;inspect.Enabled=!value;cancel.Enabled=value;if(headerCancel!=null)headerCancel.Enabled=value;search.Enabled=!value;activity.Visible=value;activity.MarqueeAnimationSpeed=value?28:0;UpdateSelection();}
  string PickFolder(string description) {using(var dialog=new FolderBrowserDialog {Description=description,ShowNewFolderButton=false,SelectedPath=policy.ProfileRoot})return dialog.ShowDialog(this)==DialogResult.OK?dialog.SelectedPath:null;}
  void ReportProgress(ScanProgress value) {lock(progressLock){latest=value;}}
  internal async Task<bool> RunScan(string title,Func<CancellationToken,Action<ScanProgress>,ScanReport> worker) {
   if(busy)return false;LeaveHome();HideCleanupWorkspace();BeginOperation(title);bool published=false;
   try {ScanReport result=await Task.Run(()=>worker(cancellation.Token,ReportProgress));pulse.Stop();lock(progressLock){latest=null;}LoadReport(result);UpdateDrive();published=true;}
   catch(OperationCanceledException) {selected.Clear();files.Invalidate();state.Text="已取消 · 未产生扫描结果";detail.Text="保留上次结果；未生成新报告，选择已清空。";}
   catch(Exception ex) {selected.Clear();files.Invalidate();state.Text="扫描失败";detail.Text=ex.Message+"；保留上次结果，选择已清空。";if(!closeRequested)MessageBox.Show(this,"扫描未完成："+ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);}
   finally {EndOperation();}
   return published;
  }
  void BeginOperation(string title,bool allowCancellation=true) {canCancel=allowCancellation;cancellation=new CancellationTokenSource();lock(progressLock){latest=null;}SetBusy(true);cancel.Enabled=canCancel;state.Text=title+" · 正在检查…";detail.Text="正在遍历文件，计数持续更新；可随时取消。";if(pet!=null&&!title.StartsWith("导出"))pet.SetPose(true,title.StartsWith("清理"));pulse.Start();}
  void EndOperation() {pulse.Stop();lock(progressLock){latest=null;}cancellation.Dispose();cancellation=null;SetBusy(false);if(pet!=null)pet.SetPose(false,false);if(closeRequested&&!IsDisposed)Close();}
  internal void PetSelectAll(){if(busy)return;if(CleanupVisible)cleanupWorkspace.SelectAllFiles(false);else SelectEligible();}
  internal async Task PetClean(){if(busy)return;if(CleanupVisible)await cleanupWorkspace.ConfirmPrepared();else await PreviewAndClean();}
  internal void PetClear(){if(busy)return;if(CleanupVisible)cleanupWorkspace.DiscardSelection();else{selected.Clear();files.Invalidate();UpdateSelection();}}
  internal async Task PetScan(int kind){if(busy)return;ShowFromPet();bool ok;if(kind==0){await OpenCompatibleCleanup(true);ok=!IsBusy;}else if(kind==1){var rules=Rules.Discover(policy.ProfileRoot);if(!inspect.Checked)rules=rules.Where(x=>x.Risk==RiskLevel.Safe).ToList();ok=await RunScan("安全缓存扫描",(t,p)=>new Scanner(policy).ScanCaches(rules,t,p));}else ok=await StartAiCaches();if(pet!=null&&ok&&!IsBusy&&state.Text.IndexOf("已取消")<0&&state.Text.IndexOf("失败")<0)pet.ShowActions();}
  internal void CancelOperation() {if(CleanupVisible&&cleanupWorkspace.IsWorking){cleanupWorkspace.CancelWork();cancel.Enabled=false;return;}if(!canCancel)return;if(cancellation!=null){cancellation.Cancel();state.Text="正在取消 · 等待当前文件操作结束…";cancel.Enabled=false;}}
  void OnClosing(object sender,FormClosingEventArgs e) {
   if(EnableDesktopPet&&pet!=null&&!pet.IsDisposed&&!allowExit){
    e.Cancel=true;
    if(askingExit)return;
    string saved=ExitChoice.Load();
    if(saved==null){askingExit=true;BeginInvoke(new Action(AskHowToLeave));return;}
    if(saved=="keep"){Hide();return;}
    allowExit=true;e.Cancel=false;
    if(busy){e.Cancel=true;closeRequested=true;CancelOperation();state.Text=canCancel?"正在停止任务，完成后关闭…":"正在导出报告，写入完成后关闭…";}
    return;
   }
   if(busy){e.Cancel=true;closeRequested=true;CancelOperation();state.Text=canCancel?"正在停止任务，完成后关闭…":"正在导出报告，写入完成后关闭…";}
  }
  void AskHowToLeave(){
   try{
    using(var dialog=new ExitChoiceForm()){
     if(dialog.ShowDialog(this)!=DialogResult.OK)return;
     string choice=dialog.KeepBackground?"keep":"full";
     if(dialog.Remember)ExitChoice.Save(choice);
     LeaveBy(choice);
    }
   }finally{askingExit=false;}
  }
  void LeaveBy(string choice){if(choice=="keep"){Hide();return;}ExitCompletely();}
  internal void ShowFromPet(){if(IsDisposed)return;if(!Visible)Show();if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;Activate();}
  protected override void WndProc(ref Message m){if(wakeMessage!=0&&(uint)m.Msg==wakeMessage){ShowFromPet();return;}base.WndProc(ref m);}
  internal void ExitCompletely(){allowExit=true;if(busy){closeRequested=true;ShowFromPet();CancelOperation();state.Text=canCancel?"正在停止任务，完成后关闭…":"正在导出报告，写入完成后关闭…";return;}Close();}
  protected override void OnFormClosed(FormClosedEventArgs e){if(pet!=null&&!pet.IsDisposed)pet.Shutdown();base.OnFormClosed(e);}
  void CopyCurrentPath() {if(files.CurrentCell==null||files.CurrentCell.RowIndex<0||files.CurrentCell.RowIndex>=visible.Count)return;try{Clipboard.SetText(visible[files.CurrentCell.RowIndex].Path);}catch(Exception ex){detail.Text="无法复制路径："+ex.Message;}}
  void OpenLocation() {if(files.CurrentCell!=null&&files.CurrentCell.RowIndex<visible.Count)LaunchLocation(visible[files.CurrentCell.RowIndex].Path,false);}
  void LaunchLocation(string path,bool directory) {try {if(String.IsNullOrEmpty(path)||!path.StartsWith(@"C:\",StringComparison.OrdinalIgnoreCase))return;if(directory&&!Directory.Exists(path))return;if(directory)Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),"\""+path+"\"");else Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),"/select,\""+path+"\"");}catch(Exception ex){MessageBox.Show(this,"无法打开文件位置："+ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Information);}}
  async Task ExportReport() {
   if(report==null||busy)return;string path;
   using(var dialog=new SaveFileDialog {Title="导出只读扫描报告",Filter="JSON 报告 (*.json)|*.json|CSV 文件明细 (*.csv)|*.csv",FileName="空间扫描-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json",AddExtension=true}) {if(dialog.ShowDialog(this)!=DialogResult.OK)return;path=dialog.FileName;}
   ScanReport value=report;BeginOperation("导出报告",false);pulse.Stop();detail.Text="正在写入只读报告；写入结束前请保留本窗口。";
   try {await Task.Run(()=>ReportWriter.WriteScan(path,value));state.Text="报告已导出";detail.Text=path;}
   catch(Exception ex){state.Text="报告导出失败";detail.Text=ex.Message;if(!closeRequested)MessageBox.Show(this,"导出失败："+ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);}
   finally{EndOperation();}
  }  internal Form CreatePreview() {
   var chosen=selected.Where(Eligible).OrderBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).ToList();
   var dialog=new Form {Text="清理预览 · 请逐项核对",Font=Font,Size=new Size(1000,650),MinimumSize=new Size(850,500),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=true,BackColor=BackColor};
   var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(18),RowCount=4,ColumnCount=1};layout.RowStyles.Add(new RowStyle(SizeType.Absolute,70));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,55));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,45));dialog.Controls.Add(layout);
   layout.Controls.Add(new Label {Dock=DockStyle.Fill,Text="即将直接删除 "+chosen.Count.ToString("N0")+" 个缓存文件，逻辑大小 "+Display.Size(chosen.Sum(x=>x.Length))+"。\r\n缓存可能在下次使用时重新生成；首次启动或加载可能变慢。请先关闭相关应用。",ForeColor=ink,TextAlign=ContentAlignment.MiddleLeft},0,0);
   var grid=new DataGridView();SetupGrid(grid);AddColumn(grid,"Size","逻辑大小",110);AddColumn(grid,"Category","分类",170);AddColumn(grid,"RuleId","规则 ID",190);AddColumn(grid,"Risk","风险 / 处理方式",150);AddColumn(grid,"Reason","清理原因",320);AddColumn(grid,"Path","完整路径",510);grid.VirtualMode=true;grid.CellValueNeeded+=delegate(object s,DataGridViewCellValueEventArgs e){if(e.RowIndex>=chosen.Count)return;var f=chosen[e.RowIndex];switch(grid.Columns[e.ColumnIndex].Name){case "Size":e.Value=Display.Size(f.Length);break;case "Category":e.Value=f.Category;break;case "RuleId":e.Value=f.RuleId;break;case "Risk":e.Value=Display.Risk(f.Risk)+" / 直接删除缓存";break;case "Reason":e.Value=f.Reason;break;case "Path":e.Value=f.Path;break;}};grid.CellToolTipTextNeeded+=delegate(object s,DataGridViewCellToolTipTextNeededEventArgs e){if(e.RowIndex>=0&&e.RowIndex<chosen.Count)e.ToolTipText=chosen[e.RowIndex].Path+"\r\n"+chosen[e.RowIndex].Reason;};grid.RowCount=chosen.Count;layout.Controls.Add(grid,0,1);
   layout.Controls.Add(new Label {Dock=DockStyle.Fill,Text="以上列出全部所选路径，可滚动查看。清理时会再次核验文件身份并跳过占用或已变化的文件。\r\n此操作不经过回收站；实际可用空间变化以完成后的测量为准。",ForeColor=Color.FromArgb(135,80,28),TextAlign=ContentAlignment.MiddleLeft},0,2);
   var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};var confirm=new Button();Configure(confirm,"确认清理",135,true);confirm.DialogResult=DialogResult.OK;confirm.Enabled=chosen.Count>0;var back=new Button();Configure(back,"返回检查",125,false);back.DialogResult=DialogResult.Cancel;buttons.Controls.Add(confirm);buttons.Controls.Add(back);layout.Controls.Add(buttons,0,3);dialog.CancelButton=back;dialog.AcceptButton=back;return dialog;
  }
  async Task PreviewAndClean() {
   if(busy||selected.Count==0)return;List<FileRecord> chosen=selected.Where(Eligible).ToList();using(Form dialog=CreatePreview())if(dialog.ShowDialog(this)!=DialogResult.OK)return;
   string logPath;
   try {string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"reports");Directory.CreateDirectory(dir);logPath=Path.Combine(dir,"clean-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".json");using(var s=new FileStream(logPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){} }
   catch(Exception ex){MessageBox.Show(this,"无法创建清理日志，未执行清理："+ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
   BeginOperation("清理缓存");
   string summary=null;bool warn=false;
   try {
    string logError=null;CleanReport result=await Task.Run(()=>{CleanReport outcome=new Cleaner(policy).Execute(chosen,false,cancellation.Token,ReportProgress);try{ReportWriter.WriteClean(logPath,outcome);}catch(Exception ex){logError=ex.Message;}return outcome;});
    pulse.Stop();lock(progressLock){latest=null;}
    var removed=new HashSet<string>(result.Results.Where(x=>x.Status=="成功").Select(x=>x.Path),StringComparer.OrdinalIgnoreCase);ReconcileRemoved(removed);UpdateDrive();
    int succeeded=result.Results.Count(x=>x.Status=="成功"),skipped=result.Results.Count(x=>x.Status=="跳过"),failed=result.Results.Count(x=>x.Status=="失败");
    if(!result.Cancelled){LoadReport(new ScanReport{Mode="cache"});state.Text="清理完成 · 列表已清空";detail.Text=skipped==0?"已删除 "+succeeded.ToString("N0")+" 项。点「返回首页」可以选择其他功能。":"已删除 "+succeeded.ToString("N0")+" 项。还有 "+skipped.ToString("N0")+" 项正被软件占用，列表已清空。";if(home!=null)home.Notice=skipped==0?"已清理 "+succeeded.ToString("N0")+" 项缓存。":"已删除 "+succeeded.ToString("N0")+" 项。还有 "+skipped.ToString("N0")+" 项正在使用，关掉 Codex、Cursor 或 DeepSeek Harness 后再清。";}
    else {state.Text="已取消 · 成功 "+succeeded+" / 跳过 "+skipped+" / 失败 "+failed;detail.Text="取消前已删除的文件不会出现在列表里。";}
    summary=(result.Cancelled?"清理已取消。":"列表已清空。")+"成功删除 "+succeeded.ToString("N0")+" 项"+(skipped>0?"，还有 "+skipped.ToString("N0")+" 项正在被 Codex、Cursor 或 DeepSeek Harness 使用，这次没有删掉。关掉这些软件后再清一次。":"。")+"\r\n点「返回首页」可以选择其他功能。\r\nC盘可用空间实测变化："+Display.Size(result.FreeAfter-result.FreeBefore)+"。\r\n"+(logError==null?"执行日志："+logPath:"日志保存失败："+logError);
    warn=logError!=null||failed>0;
   }catch(Exception ex){state.Text="清理未完成";detail.Text=ex.Message;summary="清理未完成："+ex.Message+"\r\n请重新扫描后检查。";warn=true;}
   finally{EndOperation();}
   if(!closeRequested&&summary!=null)MessageBox.Show(this,summary,Text,MessageBoxButtons.OK,warn?MessageBoxIcon.Warning:MessageBoxIcon.Information);
  }
 }
 sealed class ExitChoiceForm : Form {
  internal bool KeepBackground;
  internal bool Remember { get { return remember.Checked; } }
  readonly CheckBox remember=new CheckBox();
  internal ExitChoiceForm(){
   Text="大肥鱼空间管家";FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;ClientSize=new Size(440,196);Font=new Font("Microsoft YaHei UI",9F);BackColor=Color.White;
   Controls.Add(new Label{Text="要怎么离开？",Font=new Font(Font,FontStyle.Bold),ForeColor=Color.FromArgb(23,58,143),Bounds=new Rectangle(24,18,392,28)});
   Controls.Add(new Label{Text="完全退出会关掉桌宠。保留后台时，桌宠和托盘图标还会留着。",ForeColor=Color.FromArgb(90,104,130),Bounds=new Rectangle(24,50,392,36)});
   remember.Text="不再询问";remember.AutoSize=true;remember.ForeColor=Color.FromArgb(36,58,96);remember.Location=new Point(24,98);Controls.Add(remember);
   var keep=ChoiceButton("退出，保留后台",false);var full=ChoiceButton("完全退出",true);
   keep.SetBounds(24,140,186,36);full.SetBounds(224,140,186,36);
   keep.Click+=delegate{KeepBackground=true;DialogResult=DialogResult.OK;};
   full.Click+=delegate{KeepBackground=false;DialogResult=DialogResult.OK;};
   Controls.Add(keep);Controls.Add(full);
  }
  static Button ChoiceButton(string text,bool primary){
   var button=new Button{Text=text,FlatStyle=FlatStyle.Flat,Cursor=Cursors.Hand,UseVisualStyleBackColor=false};
   button.FlatAppearance.BorderSize=primary?0:1;button.FlatAppearance.BorderColor=Color.FromArgb(186,206,236);
   button.BackColor=primary?Color.FromArgb(32,78,196):Color.White;button.ForeColor=primary?Color.White:Color.FromArgb(32,78,196);
   return button;
  }
 }
 static class ExitChoice {
  static string PathOf(){return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DaFeiYu","close.txt");}
  internal static string Load(){try{string path=PathOf();if(!File.Exists(path))return null;string text=File.ReadAllText(path).Trim();return text=="full"||text=="keep"?text:null;}catch{return null;}}
  internal static void Save(string value){try{string path=PathOf();Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,value);}catch{}}
 }
}






