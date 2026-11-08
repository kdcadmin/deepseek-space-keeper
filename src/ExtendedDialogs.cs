using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ClearDisk {
 internal static class ExtendedDialogs {
  internal static Form CreateConfirmation(string title,string explanation,List<string[]> rows,string action,bool permanent){
   var form=new Form {Text=title,Font=new Font("Microsoft YaHei UI",9),Size=new Size(1060,670),MinimumSize=new Size(850,520),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false};
   var panel=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=1,RowCount=4};panel.RowStyles.Add(new RowStyle(SizeType.Absolute,80));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,45));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,42));form.Controls.Add(panel);
   panel.Controls.Add(new Label {Text=explanation,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,0);
   var grid=new DataGridView {Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,RowHeadersVisible=false,AutoGenerateColumns=false,BackgroundColor=Color.White,VirtualMode=true};
   foreach(var name in new[]{"大小","分类 / 状态","原因 / 提示","完整原路径"})grid.Columns.Add(new DataGridViewTextBoxColumn{HeaderText=name,Width=name=="完整原路径"?450:name=="原因 / 提示"?350:120});grid.CellValueNeeded+=delegate(object s,DataGridViewCellValueEventArgs e){e.Value=rows[e.RowIndex][e.ColumnIndex];};grid.RowCount=rows.Count;panel.Controls.Add(grid,0,1);
   var acknowledged=new CheckBox {Text=permanent?"我已核对以上文件，确认彻底删除后无法恢复。":"我已核对这些个人文件，了解隔离可能影响相关软件读取。",Dock=DockStyle.Fill};panel.Controls.Add(acknowledged,0,2);
   var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};var confirm=new Button {Text=action,DialogResult=DialogResult.OK,Width=165,Height=32,Enabled=false};var cancel=new Button {Text="返回检查",DialogResult=DialogResult.Cancel,Width=120,Height=32};buttons.Controls.Add(confirm);buttons.Controls.Add(cancel);panel.Controls.Add(buttons,0,3);acknowledged.CheckedChanged+=delegate {confirm.Enabled=acknowledged.Checked&&rows.Count>0;};form.CancelButton=cancel;form.AcceptButton=cancel;return form;
  }
  internal static Form CreateCatalog(){
   var catalog=CommunityRules.LoadBundled().Catalog;List<CommunityEntry> shown=catalog;
   var form=new Form {Text="离线规则库 · "+catalog.Count.ToString("N0")+" 条 · Winapp2 260915",Font=new Font("Microsoft YaHei UI",9),Size=new Size(1120,720),MinimumSize=new Size(850,530),StartPosition=FormStartPosition.CenterParent};
   var panel=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(14),RowCount=3,ColumnCount=1};panel.RowStyles.Add(new RowStyle(SizeType.Absolute,55));panel.RowStyles.Add(new RowStyle(SizeType.Absolute,38));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));form.Controls.Add(panel);
   panel.Controls.Add(new Label {Text="社区定义可包含个人数据、账号信息和系统路径。只读扫描支持当前用户目录内的 FileKey 子集，不执行注册表、命令或删除操作。\r\n来源：MoscaDotTo/Winapp2，CC-BY-SA-4.0；原始规则及许可证随源码附带。条目数量不代表本机适用数量或可清理空间。",Dock=DockStyle.Fill},0,0);
   var search=new TextBox {Dock=DockStyle.Fill,Margin=new Padding(0,5,0,8),AccessibleName="搜索规则名称"};panel.Controls.Add(search,0,1);
   var grid=new DataGridView {Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,RowHeadersVisible=false,VirtualMode=true,BackgroundColor=Color.White,AutoGenerateColumns=false};
   foreach(var column in new[]{new DataGridViewTextBoxColumn{HeaderText="条目",Width=420},new DataGridViewTextBoxColumn{HeaderText="FileKey 数",Width=100},new DataGridViewTextBoxColumn{HeaderText="RegKey 数（不执行）",Width=165},new DataGridViewTextBoxColumn{HeaderText="提醒 / 原始定义（选中后显示）",Width=750}})grid.Columns.Add(column);
   grid.CellValueNeeded+=delegate(object s,DataGridViewCellValueEventArgs e){var entry=shown[e.RowIndex];e.Value=e.ColumnIndex==0?(object)entry.Name:e.ColumnIndex==1?entry.FileKeys.Count:e.ColumnIndex==2?(object)entry.RegistryKeys:(entry.Warning+" | "+String.Join(" || ",entry.FileKeys));};grid.RowCount=shown.Count;panel.Controls.Add(grid,0,2);
   search.TextChanged+=delegate {string q=search.Text.Trim();shown=catalog.Where(x=>x.Name.IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0).ToList();grid.RowCount=shown.Count;grid.Invalidate();};return form;
  }
 }
}
