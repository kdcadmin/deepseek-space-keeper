using System;
using System.Drawing;
using System.Windows.Forms;

namespace ClearDisk {
 internal sealed class MediaSearchDialog:Form {
  readonly TextBox root=new TextBox();readonly ComboBox kind=new ComboBox();readonly NumericUpDown minimum=new NumericUpDown(),threads=new NumericUpDown();
  internal string SearchRoot {get{return root.Text;}}
  internal MediaSearchOptions Options {get{return new MediaSearchOptions{Kind=(MediaKind)kind.SelectedIndex,MinimumBytes=Math.Max(1,(long)minimum.Value*1048576),Threads=(int)threads.Value};}}
  internal MediaSearchDialog(SafetyPolicy policy){
   Text="相似文件查找";Font=new Font("Microsoft YaHei UI",9);Size=new Size(690,405);MinimumSize=Size;StartPosition=FormStartPosition.CenterParent;MinimizeBox=false;MaximizeBox=false;
   var panel=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=3,RowCount=6};panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110));panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90));Controls.Add(panel);
   foreach(int height in new[]{45,45,45,45,90,50})panel.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
   panel.Controls.Add(new Label{Text="扫描目录",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,0);root.ReadOnly=true;root.Text=policy.ProfileRoot;root.Dock=DockStyle.Fill;root.Margin=new Padding(0,8,8,0);panel.Controls.Add(root,1,0);
   var browse=new Button{Text="选择…",Dock=DockStyle.Fill,Margin=new Padding(0,4,0,6)};panel.Controls.Add(browse,2,0);browse.Click+=delegate{using(var picker=new FolderBrowserDialog{Description="选择普通个人媒体目录",SelectedPath=root.Text,ShowNewFolderButton=false})if(picker.ShowDialog(this)==DialogResult.OK)root.Text=picker.SelectedPath;};
   panel.Controls.Add(new Label{Text="文件类型",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,1);kind.DropDownStyle=ComboBoxStyle.DropDownList;kind.Items.AddRange(new object[]{"相似图片","相似视频","音乐标签相似","音乐音频内容相似"});kind.SelectedIndex=0;kind.Dock=DockStyle.Fill;kind.Margin=new Padding(0,8,8,0);panel.Controls.Add(kind,1,1);
   panel.Controls.Add(new Label{Text="最小大小 (MB)",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,2);minimum.Maximum=1048576;minimum.Dock=DockStyle.Fill;minimum.Margin=new Padding(0,8,8,0);panel.Controls.Add(minimum,1,2);
   panel.Controls.Add(new Label{Text="并行线程",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,3);threads.Minimum=1;threads.Maximum=16;threads.Value=4;threads.Dock=DockStyle.Fill;threads.Margin=new Padding(0,8,8,0);panel.Controls.Add(threads,1,3);
   var note=new Label{Text="使用原版同版本 Czkawka 10.0.0 引擎，只读比较。视频需要已安装 FFmpeg / ffprobe。\r\n相似不代表内容完全相同；音乐标签相同也不代表录音相同。每组保留一项，默认不选，请人工核对后再整理。",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};panel.Controls.Add(note,0,4);panel.SetColumnSpan(note,3);
   var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};var scan=new Button{Text="开始扫描",Size=new Size(110,32)};var cancel=new Button{Text="取消",DialogResult=DialogResult.Cancel,Size=new Size(90,32)};buttons.Controls.Add(scan);buttons.Controls.Add(cancel);panel.Controls.Add(buttons,0,5);panel.SetColumnSpan(buttons,3);CancelButton=cancel;AcceptButton=scan;
   scan.Click+=delegate{if(!SafetyPolicy.IsWithin(root.Text,policy.ProfileRoot)||!policy.ValidateRoot(root.Text)){MessageBox.Show(this,"请选择当前用户目录中的普通文件夹，受保护的系统、项目及环境目录不能用于媒体整理。",Text);return;}DialogResult=DialogResult.OK;};
  }
 }
}
