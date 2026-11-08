using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClearDisk {
 public sealed partial class MainForm {
  readonly Button referenceButton=new Button(),special=new Button(),catalogButton=new Button(),historyButton=new Button(),organizeButton=new Button(),compatibleButton=new Button(),safeCaches=new Button();
  readonly CheckBox organizationMode=new CheckBox();
  readonly ComboBox ageChoice=new ComboBox(),sizeChoice=new ComboBox();
  readonly TabPage summaryTab=new TabPage("分类概览");
  readonly DataGridView summaryGrid=new DataGridView();
  TabPage referenceTab;ReferenceResultsView referenceResults;
  int filterAgeDays,customAgeDays;long filterMinimumBytes,customMinimumBytes;bool settingExtraFilters;
  void InitializeExtendedActions(FlowLayoutPanel actions){
   compatibleButton.Name="compatibleCleanupButton";Configure(compatibleButton,"查看完整结果",150,false);actions.Controls.Add(compatibleButton);var administrationButton=new Button();Configure(administrationButton,"系统 / 软件管理…",170,false);administrationButton.Click+=delegate{if(!busy)using(var form=new AdvancedAdministrationForm())form.ShowDialog(this);};actions.Controls.Add(administrationButton);var releasesButton=new Button();Configure(releasesButton,"规则版本 / 诊断…",160,false);releasesButton.Click+=delegate{if(!busy)using(var form=new RuleSettingsForm())form.ShowDialog(this);};actions.Controls.Add(releasesButton);compatibleButton.Click+=async delegate{await OpenCompatibleCleanup(false);};
   safeCaches.Name="safeCacheScanButton";Configure(safeCaches,"安全缓存扫描",145,false);actions.Controls.Add(safeCaches);safeCaches.Click+=async delegate{if(busy)return;var rules=Rules.Discover(policy.ProfileRoot);if(!inspect.Checked)rules=rules.Where(x=>x.Risk==RiskLevel.Safe).ToList();await RunScan("安全缓存扫描",(t,p)=>new Scanner(policy).ScanCaches(rules,t,p));};
   Configure(referenceButton,"原版范围对照（只读）",190,false);actions.Controls.Add(referenceButton);
   referenceButton.Click+=async delegate{await RunScan("原版范围对照",(t,p)=>CommunityScanner.CreateReferenceInventory(policy).Scan(t,p));};
   Configure(special,"专项扫描 ▾",125,false);Configure(catalogButton,"4,068 条规则库",155,false);Configure(historyButton,"隔离区 / 恢复",145,false);
   actions.Controls.AddRange(new Control[]{special,catalogButton,historyButton});
   special.Click+=delegate {
    var menu=new ContextMenuStrip();
    AddScanMenu(menu,"原版规则对照扫描（含系统占用，只读）",(t,p)=>CommunityScanner.CreateReferenceInventory(policy).Scan(t,p));
    var elevated=new ToolStripMenuItem("管理员只读对照窗口（需手动同意系统提示）");elevated.Click+=delegate{ReferenceInventoryForm.OpenElevated(this);};menu.Items.Add(elevated);
    var software=new ToolStripMenuItem("软件占用查询…");software.Click+=delegate{using(var dialog=new SoftwareInventoryForm(policy))dialog.ShowDialog(this);};menu.Items.Add(software);
    var directory=new ToolStripMenuItem("目录大小查看 / 逐层进入…");directory.Click+=delegate{using(var dialog=new DirectoryUsageForm(policy))dialog.ShowDialog(this);};menu.Items.Add(directory);
    AddScanMenu(menu,"社区规则广泛扫描（只读）",(t,p)=>new CommunityScanner(policy).Scan(t,p));
    var media=new ToolStripMenuItem("相似图片 / 视频 / 音乐…");media.Click+=async delegate {using(var dialog=new MediaSearchDialog(policy)){if(dialog.ShowDialog(this)!=DialogResult.OK)return;string root=dialog.SearchRoot;MediaSearchOptions options=dialog.Options;await RunScan("相似文件查找",(t,p)=>new MediaSimilarityScanner(policy).Scan(root,options,t,p));}};menu.Items.Add(media);
    AddChatMenu(menu,"微信 4 附件检查…",ChatProduct.WeChat4);
    AddChatMenu(menu,"QQ 补清 / Group2 图片…",ChatProduct.QQGroup2);
    AddScanMenu(menu,"下载文件整理",(t,p)=>new InspectionScanner(policy).ScanDownloads(t,p));
    AddScanMenu(menu,"其他应用缓存探测（只读）",(t,p)=>new InspectionScanner(policy).ScanUnknownCaches(t,p));
    AddScanMenu(menu,"系统占用说明（只读）",(t,p)=>new InspectionScanner(policy).ScanSystem(t,p));
    menu.Closed+=delegate {menu.Dispose();};menu.Show(special,new Point(0,special.Height));
   };
   catalogButton.Click+=delegate {using(var form=ExtendedDialogs.CreateCatalog())form.ShowDialog(this);};
   historyButton.Click+=delegate {using(var form=new QuarantineForm(policy))form.ShowDialog(this);UpdateDrive();};
   Control admin=actions.Controls.Cast<Control>().First(c=>c is Button&&((Button)c).Text.StartsWith("系统 / 软件"));
   Control releases=actions.Controls.Cast<Control>().First(c=>c is Button&&((Button)c).Text.StartsWith("规则版本"));
   int index=0;foreach(Control control in new[]{caches,safeCaches,inspect,compatibleButton,cancel,disk,choose,duplicates,special,admin,releases,historyButton,catalogButton,referenceButton})actions.Controls.SetChildIndex(control,index++);
  }
  void AddScanMenu(ContextMenuStrip menu,string title,Func<CancellationToken,Action<ScanProgress>,ScanReport> scan){var item=new ToolStripMenuItem(title);item.Click+=async delegate {await RunScan(title,scan);};menu.Items.Add(item);}
  void AddChatMenu(ContextMenuStrip menu,string title,ChatProduct product){var item=new ToolStripMenuItem(title);item.Click+=async delegate{using(var dialog=new ChatCleanupDialog(policy,product)){if(dialog.ShowDialog(this)!=DialogResult.OK)return;ChatScanOptions options=dialog.Options;await RunScan(title,(t,p)=>new ChatInventoryScanner(policy).Scan(options,t,p));}};menu.Items.Add(item);}
  void InitializeExtraFilters(TableLayoutPanel filter){
   filter.Controls.Remove(counts);filter.ColumnCount=5;filter.ColumnStyles.Clear();
   filter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,75));filter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));filter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,135));filter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,140));filter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,190));
   foreach(var c in new[]{ageChoice,sizeChoice}){c.DropDownStyle=ComboBoxStyle.DropDownList;c.Dock=DockStyle.Fill;c.Margin=new Padding(0,6,10,5);}
   ageChoice.Items.AddRange(new object[]{"全部修改时间","超过 7 天","超过 30 天","超过 90 天","超过 180 天","超过 365 天"});
   sizeChoice.Items.AddRange(new object[]{"全部文件大小","至少 1 MB","至少 10 MB","至少 100 MB","至少 1 GB"});ageChoice.SelectedIndex=0;sizeChoice.SelectedIndex=0;
   filter.Controls.Add(ageChoice,2,0);filter.Controls.Add(sizeChoice,3,0);filter.Controls.Add(counts,4,0);
   ageChoice.SelectedIndexChanged+=delegate {if(settingExtraFilters||ageChoice.SelectedIndex<0)return;filterAgeDays=ageChoice.SelectedIndex<6?new[]{0,7,30,90,180,365}[ageChoice.SelectedIndex]:customAgeDays;ApplyFilter();};
   sizeChoice.SelectedIndexChanged+=delegate {if(settingExtraFilters||sizeChoice.SelectedIndex<0)return;filterMinimumBytes=sizeChoice.SelectedIndex<5?new long[]{0,1048576,10485760,104857600,1073741824}[sizeChoice.SelectedIndex]:customMinimumBytes;ApplyFilter();};
  }
  void InitializeOrganizationTools(FlowLayoutPanel tools){
   organizationMode.Text="手动选个人文件";organizationMode.AutoSize=true;organizationMode.Margin=new Padding(2,10,8,0);
   Configure(organizeButton,"预览并隔离…",145,false);tools.Controls.Add(organizationMode);tools.Controls.Add(organizeButton);
   organizationMode.CheckedChanged+=delegate {selected.Clear();files.Invalidate();UpdateSelection();};organizeButton.Click+=async delegate {await OrganizeSelected();};
  }
  // Display uses the scanner's cached eligibility; never wait on the executor lock during paint.
  // The executor validates the private scan snapshot again before every mutation.
  bool Organizable(FileRecord file){return report!=null&&new[]{"disk","duplicates","downloads","chat","similar"}.Contains(report.Mode)&&file.CanOrganize&&file.Stamp!=null&&!file.IsKeeper&&file.Risk!=RiskLevel.Protected;}
  bool Selectable(FileRecord file){return organizationMode.Checked?Organizable(file):Eligible(file);}
  bool ExtraFilter(FileRecord file){return file.Length>=filterMinimumBytes&&(filterAgeDays==0||file.LastWriteTicks>0&&file.LastWriteTicks<=DateTime.UtcNow.AddDays(-filterAgeDays).Ticks);}
  void ResetExtraFilters(){ageChoice.SelectedIndex=0;sizeChoice.SelectedIndex=0;filterAgeDays=0;filterMinimumBytes=0;organizationMode.Checked=false;}
  internal void SetExtraFilters(int days,long bytes){
   if(days<0||days>36500||bytes<0)throw new ArgumentOutOfRangeException();settingExtraFilters=true;
   try{int ageIndex=Array.IndexOf(new[]{0,7,30,90,180,365},days);if(ageIndex<0){while(ageChoice.Items.Count>6)ageChoice.Items.RemoveAt(6);customAgeDays=days;ageChoice.Items.Add("超过 "+days+" 天（自定义）");ageIndex=6;}ageChoice.SelectedIndex=ageIndex;
    int sizeIndex=Array.IndexOf(new long[]{0,1048576,10485760,104857600,1073741824},bytes);if(sizeIndex<0){while(sizeChoice.Items.Count>5)sizeChoice.Items.RemoveAt(5);customMinimumBytes=bytes;sizeChoice.Items.Add("至少 "+(bytes%1048576==0?(bytes/1048576).ToString("N0")+" MB":Display.Size(bytes))+"（自定义）");sizeIndex=5;}sizeChoice.SelectedIndex=sizeIndex;filterAgeDays=days;filterMinimumBytes=bytes;
   }finally{settingExtraFilters=false;}ApplyFilter();
  }
  internal void SetOrganizationMode(bool value){organizationMode.Checked=value;}
  internal void SetManualSelection(string path,bool value){var file=visible.FirstOrDefault(x=>String.Equals(x.Path,path,StringComparison.OrdinalIgnoreCase));if(file==null)return;if(!busy&&Selectable(file)&&value)selected.Add(file);else selected.Remove(file);files.Invalidate();UpdateSelection();}
  internal void ReconcileRemoved(HashSet<string> paths){report.Files.RemoveAll(x=>paths.Contains(x.Path));selected.RemoveWhere(x=>paths.Contains(x.Path));PopulateSummary();ApplyFilter();UpdateSelection();}
  void UpdateExtendedSelection(){
   organizeButton.Enabled=!busy&&organizationMode.Checked&&selected.Any(Organizable);organizationMode.Enabled=!busy&&report!=null&&new[]{"disk","duplicates","downloads","chat","similar"}.Contains(report.Mode);
   all.Enabled=all.Enabled&&!organizationMode.Checked;preview.Enabled=preview.Enabled&&!organizationMode.Checked;
   safeCaches.Enabled=!busy;compatibleButton.Enabled=!busy;referenceButton.Enabled=!busy;special.Enabled=!busy;catalogButton.Enabled=!busy;historyButton.Enabled=!busy;ageChoice.Enabled=!busy;sizeChoice.Enabled=!busy;UpdateNavigation();
  }
  void PopulateSummary(){
   if(!tabs.TabPages.Contains(summaryTab)){
    var panel=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};panel.RowStyles.Add(new RowStyle(SizeType.Absolute,43));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
    panel.Controls.Add(new Label {Text="目录占用为扫描时快照，清理后需重新扫描刷新。可直接清理量按本批剩余明细更新；社区条目及父子目录可能重叠，不能相加。",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(10)},0,0);
    SetupGrid(summaryGrid);AddColumn(summaryGrid,"Category","分类 / 扫描目录",520);AddColumn(summaryGrid,"Logical","扫描时逻辑大小",150);AddColumn(summaryGrid,"Files","扫描时文件数",120);AddColumn(summaryGrid,"Cleanable","本批剩余可直接清理",170);panel.Controls.Add(summaryGrid,0,1);summaryTab.Controls.Add(panel);tabs.TabPages.Add(summaryTab);
   }
   summaryGrid.Rows.Clear();
   var known=Rules.Discover(policy.ProfileRoot).ToDictionary(x=>x.Root,x=>x.Name,StringComparer.OrdinalIgnoreCase);
   foreach(var folder in report.Folders.OrderByDescending(x=>x.Bytes)){
    string category;known.TryGetValue(folder.Path,out category);if(category==null)category=folder.Path;
    long eligible=report.Mode=="cache"?report.Files.Where(x=>x.CanDelete&&String.Equals(x.Root,folder.Path,StringComparison.OrdinalIgnoreCase)).Sum(x=>x.Length):0;
    summaryGrid.Rows.Add(category,Display.Size(folder.Bytes),folder.Files.ToString("N0"),Display.Size(eligible));
   }
   summaryTab.Text="分类概览 ("+report.Folders.Count.ToString("N0")+")";
  }
  void PopulateReferenceResults(){
   if(report.Mode=="reference-inventory"){
    if(referenceResults==null){referenceResults=new ReferenceResultsView();referenceTab=new TabPage("原版分组（只读）");referenceTab.Controls.Add(referenceResults);tabs.TabPages.Add(referenceTab);}
    referenceResults.LoadReport(report);tabs.SelectedTab=referenceTab;
   }else if(referenceResults!=null){tabs.TabPages.Remove(referenceTab);referenceTab.Dispose();referenceTab=null;referenceResults=null;}
  }
  internal Form CreateOrganizationPreview(){
   var chosen=selected.Where(Organizable).ToList();
   return ExtendedDialogs.CreateConfirmation("个人文件隔离预览","将移动 "+chosen.Count.ToString("N0")+" 个文件到当前用户的可恢复隔离区，逻辑大小 "+Display.Size(chosen.Sum(x=>x.Length))+"。\r\n这一步不释放 C 盘空间，也不经过 Windows 回收站；原软件可能暂时无法读取这些附件。",chosen.Select(x=>new[]{Display.Size(x.Length),x.Category??"个人文件",x.Reason??"",x.Path}).ToList(),"确认移入隔离区",false);
  }
  async Task OrganizeSelected(){
   if(busy||!organizationMode.Checked)return;var chosen=selected.Where(x=>Organizable(x)&&policy.CanOrganize(x)).ToList();if(chosen.Count==0)return;
   using(var confirm=CreateOrganizationPreview())if(confirm.ShowDialog(this)!=DialogResult.OK)return;
   BeginOperation("逐项隔离个人文件");
   try {
    var results=new List<CleanResult>();var store=new QuarantineStore(policy);
    await Task.Run(()=>{foreach(var f in chosen){if(cancellation.IsCancellationRequested)break;string receipt,reason;bool ok;try{ok=store.Move(f,out receipt,out reason,cancellation.Token);}catch(OperationCanceledException){break;}results.Add(new CleanResult {Path=f.Path,Action="移入可恢复隔离区",Status=ok?"成功":"跳过",Reason=reason+(ok?" 记录："+receipt:""),Bytes=f.Length});ReportProgress(new ScanProgress{Files=results.Count,Bytes=results.Where(x=>x.Status=="成功").Sum(x=>x.Bytes),Message=f.Path});}});
    pulse.Stop();lock(progressLock){latest=null;}
    var moved=new HashSet<string>(results.Where(x=>x.Status=="成功").Select(x=>x.Path),StringComparer.OrdinalIgnoreCase);ReconcileRemoved(moved);selected.Clear();UpdateSelection();UpdateDrive();
    state.Text=(cancellation.IsCancellationRequested?"隔离已取消":"隔离完成")+" · 成功 "+moved.Count+" / 跳过 "+results.Count(x=>x.Status!="成功");detail.Text="隔离不释放 C 盘空间。请通过“隔离区 / 恢复”检查、还原或另行确认彻底删除。";
    bool wasCancelled=cancellation.IsCancellationRequested;string log=await Task.Run(()=>SaveOrganizationLog(results,wasCancelled));if(!closeRequested)MessageBox.Show(this,state.Text+"\r\n"+detail.Text+"\r\n"+log,Text,MessageBoxButtons.OK,MessageBoxIcon.Information);
   }catch(Exception e){state.Text="整理未完成";detail.Text=e.Message;if(!closeRequested)MessageBox.Show(this,"整理未完成："+e.Message+"\r\n已经移动的文件可在隔离区检查和恢复。",Text);}
   finally {EndOperation();}
  }
  string SaveOrganizationLog(List<CleanResult> results,bool wasCancelled){try {string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"reports");Directory.CreateDirectory(dir);string log=Path.Combine(dir,"organize-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".json");ReportWriter.WriteClean(log,new CleanReport{Results=results,Cancelled=wasCancelled});return "操作日志："+log;}catch(Exception e){return "操作日志保存失败："+e.Message+"；隔离恢复记录已单独保存。";}}
 }
}
