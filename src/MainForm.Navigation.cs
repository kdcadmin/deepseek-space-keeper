using System;using System.Collections.Generic;using System.Drawing;using System.Linq;using System.Threading.Tasks;using System.Windows.Forms;
namespace ClearDisk {
 public sealed partial class MainForm {
  internal enum NavigationFeature {Basic,System,WeChat,QQ,UserMigration,Software,SoftwareMigration,Large,Duplicates,Similar,Directory}
  readonly Dictionary<NavigationFeature,Button> navigation=new Dictionary<NavigationFeature,Button>();
  HomeSurface home;
  internal Func<bool,MigrationDialog> MigrationDialogFactory=portable=>new MigrationDialog(portable);
  NavigationFeature activeFeature=NavigationFeature.Basic;
  void InitializeNavigation(Control body){
   Controls.Remove(body);var shell=new Panel{Dock=DockStyle.Fill,BackColor=Color.White};Controls.Add(shell);
   var rail=new FlowLayoutPanel{Dock=DockStyle.Left,Width=0,Visible=false,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(12,18,10,16),BackColor=Color.FromArgb(239,246,255)};body.Dock=DockStyle.Fill;body.Margin=Padding.Empty;shell.Controls.Add(body);shell.Controls.Add(rail);
   rail.Controls.Add(new Label{Text="空间管理",Font=new Font(Font.FontFamily,13,FontStyle.Bold),ForeColor=blue,Size=new Size(150,45),TextAlign=ContentAlignment.MiddleLeft});
   AddNavigation(rail,NavigationFeature.Basic,"基础清理");AddNavigation(rail,NavigationFeature.System,"系统清理 / 维护");AddNavigation(rail,NavigationFeature.WeChat,"微信清理");AddNavigation(rail,NavigationFeature.QQ,"QQ补清");AddNavigation(rail,NavigationFeature.UserMigration,"用户子目录迁移");AddNavigation(rail,NavigationFeature.Software,"软件占用查询");AddNavigation(rail,NavigationFeature.SoftwareMigration,"软件搬家 · 便携");AddNavigation(rail,NavigationFeature.Large,"大文件查找");AddNavigation(rail,NavigationFeature.Duplicates,"重复文件查找");AddNavigation(rail,NavigationFeature.Similar,"相似文件查找");AddNavigation(rail,NavigationFeature.Directory,"目录大小查看");
   rail.Controls.Add(new Label{Text="系统垃圾可预览清理\r\n扫描不会自动清理",Size=new Size(158,62),ForeColor=Color.FromArgb(78,100,94),Padding=new Padding(6,10,0,0)});
   SetNavigation(NavigationFeature.Basic);MinimumSize=new Size(1260,800);Size=new Size(1480,900);
   home=new HomeSurface{Dock=DockStyle.Fill};
   home.DriveChosen+=async delegate{await OpenCompatibleCleanup(true);};
   home.JunkChosen+=delegate{if(!busy)safeCaches.PerformClick();};
   home.AiChosen+=async delegate{await StartAiCaches();};
   shell.Controls.Add(home);home.BringToFront();Shown+=delegate{if(home!=null&&home.Visible)home.BringToFront();};
  }
  void LeaveHome(){if(home==null)return;home.Notice=null;home.Visible=false;}
  void ShowHome(){if(busy||home==null)return;if(CleanupVisible)HideCleanupWorkspace();LoadReport(new ScanReport{Mode="cache"});state.Text="就绪 · 尚未扫描";detail.Text="从首页选择下一项。";home.Visible=true;home.BringToFront();}
  async Task<bool> StartAiCaches(){if(busy)return false;return await RunScan("AI 缓存清理",(t,p)=>new Scanner(policy).ScanCaches(Rules.AiCaches(policy.ProfileRoot),t,p));}
  void AddNavigation(FlowLayoutPanel rail,NavigationFeature feature,string text){var button=new Button{Text=text,Name="nav"+feature,AccessibleName=text,Size=new Size(168,38),FlatStyle=FlatStyle.Flat,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(12,0,0,0),Margin=new Padding(0,2,0,2),Cursor=Cursors.Hand,ForeColor=ink,BackColor=Color.FromArgb(239,246,255)};button.FlatAppearance.BorderSize=0;button.FlatAppearance.MouseOverBackColor=Color.White;navigation.Add(feature,button);rail.Controls.Add(button);button.Click+=async delegate{await NavigateFeature(feature);};}
  void SetNavigation(NavigationFeature feature){activeFeature=feature;foreach(var entry in navigation){bool on=entry.Key==feature;entry.Value.BackColor=on?Color.White:Color.FromArgb(239,246,255);entry.Value.ForeColor=on?blue:ink;}}
  internal async Task NavigateFeature(NavigationFeature feature){
   if(busy)return;NavigationFeature previous=activeFeature;SetNavigation(feature);bool showResult=false;
   try{
    if(feature==NavigationFeature.Basic){if(cleanupWorkspace!=null)await OpenCompatibleCleanup(false);else{LoadReport(new ScanReport{Mode="cache"});state.Text="尚未扫描";detail.Text="点击“完整规则扫描”，在主界面检查系统/软件并清理；“安全缓存扫描”为小范围可信缓存。";}showResult=true;}
    else if(feature==NavigationFeature.System){using(var dialog=new SystemMaintenanceForm())dialog.ShowDialog(this);}
    else if(feature==NavigationFeature.WeChat||feature==NavigationFeature.QQ){using(var dialog=new ChatCleanupDialog(policy,feature==NavigationFeature.WeChat?ChatProduct.WeChat4:ChatProduct.QQGroup2)){if(dialog.ShowDialog(this)!=DialogResult.OK)return;ChatScanOptions options=dialog.Options;showResult=await RunScan(feature==NavigationFeature.WeChat?"微信附件检查":"QQ Group2图片检查",(t,p)=>new ChatInventoryScanner(policy).Scan(options,t,p));}}
    else if(feature==NavigationFeature.UserMigration||feature==NavigationFeature.SoftwareMigration){
     try{using(var dialog=MigrationDialogFactory(feature==NavigationFeature.SoftwareMigration))dialog.ShowDialog(this);}
     catch(Exception error){if(!ScanWalker.IsFileError(error))throw;state.Text="迁移暂不可用";detail.Text="未启动迁移："+error.Message;}
    }
    else if(feature==NavigationFeature.Software){using(var dialog=new SoftwareInventoryForm(policy))dialog.ShowDialog(this);}
    else if(feature==NavigationFeature.Directory){using(var dialog=new DirectoryUsageForm(policy))dialog.ShowDialog(this);}
    else if(feature==NavigationFeature.Similar){using(var dialog=new MediaSearchDialog(policy)){if(dialog.ShowDialog(this)!=DialogResult.OK)return;var options=dialog.Options;string root=dialog.SearchRoot;showResult=await RunScan("相似文件查找",(t,p)=>new MediaSimilarityScanner(policy).Scan(root,options,t,p));}}
    else {using(var dialog=new FileSearchDialog(policy,feature==NavigationFeature.Large?FileSearchKind.LargeFiles:FileSearchKind.Duplicates)){if(dialog.ShowDialog(this)!=DialogResult.OK)return;var options=dialog.Options;if(feature==NavigationFeature.Large)showResult=await RunScan("大文件查找",(t,p)=>new Scanner(policy).ScanDisk(options.SearchRoot,t,p));else showResult=await RunScan("重复文件查找",(t,p)=>new DuplicateFinder(policy).Scan(options.SearchRoot,t,p));if(showResult)SetExtraFilters(0,options.MinimumBytes);}}
   }finally{if(!IsDisposed)SetNavigation(showResult?feature:previous);}
  }
  void UpdateNavigation(){foreach(var button in navigation.Values)button.Enabled=!busy;}
 }
}
