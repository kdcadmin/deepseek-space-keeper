using System;using System.Drawing;using System.Windows.Forms;
namespace ClearDisk {
 internal sealed class SystemProtectionDialog:Form {
  internal SystemProtectionDialog(){
   Text="系统占用 · 系统文件保护";Font=new Font("Microsoft YaHei UI",9);Size=new Size(810,570);MinimumSize=Size;StartPosition=FormStartPosition.CenterParent;BackColor=Color.FromArgb(244,250,248);
   var panel=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(22),ColumnCount=1,RowCount=4};panel.RowStyles.Add(new RowStyle(SizeType.Absolute,75));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,70));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,42));Controls.Add(panel);
   panel.Controls.Add(new Label{Text="按照你的要求，系统文件保持只读。\r\n可以查看系统目录占用；无权限位置会显示为未知。",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Font=new Font(Font.FontFamily,11)},0,0);
   var list=new ListView{Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,HeaderStyle=ColumnHeaderStyle.Nonclickable};list.Columns.Add("原版操作",290);list.Columns.Add("本工具的处理",390);foreach(string text in new[]{"关闭休眠 / 系统还原","转移或还原虚拟内存","DISM组件清理 / 驱动与补丁","删除系统日志 / 使用痕迹","回收站 / 下载目录一键清理"})list.Items.Add(new ListViewItem(new[]{text,"保持受保护，不执行此操作"}));panel.Controls.Add(list,0,1);
   panel.Controls.Add(new Label{Text="查看范围：Windows临时目录、日志、更新目录、组件与安装缓存、系统管理的虚拟内存文件等。\r\n占用大小不代表可释放空间。个人下载文件可从主界面的专项扫描中逐项检查。",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,2);
   var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};var scan=new Button{Text="查看系统占用",Size=new Size(150,32),DialogResult=DialogResult.OK};var cancel=new Button{Text="返回",Size=new Size(110,32),DialogResult=DialogResult.Cancel};actions.Controls.Add(scan);actions.Controls.Add(cancel);panel.Controls.Add(actions,0,3);AcceptButton=cancel;CancelButton=cancel;
  }
 }
}
