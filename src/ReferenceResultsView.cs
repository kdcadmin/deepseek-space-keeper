using System;using System.Collections.Generic;using System.Drawing;using System.Linq;using System.Windows.Forms;
namespace ClearDisk {
 internal sealed class ReferenceResultsView:UserControl {
  readonly TreeView tree=new TreeView();readonly Label summary=new Label();readonly Button details=new Button();
  readonly Dictionary<string,CommunityEntry> definitions;ScanReport report;
  internal ReferenceResultsView(){
   Dock=DockStyle.Fill;BackColor=Color.FromArgb(239,247,244);Padding=new Padding(12);
   definitions=CommunityRules.LoadReferenceInventory().Catalog.GroupBy(e=>e.Name,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
   var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3};layout.RowStyles.Add(new RowStyle(SizeType.Absolute,60));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));Controls.Add(layout);
   summary.Dock=DockStyle.Fill;summary.TextAlign=ContentAlignment.MiddleLeft;layout.Controls.Add(summary,0,0);
   tree.Dock=DockStyle.Fill;tree.BorderStyle=BorderStyle.None;tree.BackColor=BackColor;tree.ForeColor=Color.FromArgb(36,63,55);tree.Font=new Font("Microsoft YaHei UI",10);tree.ItemHeight=36;tree.ShowLines=false;tree.HideSelection=false;tree.FullRowSelect=true;tree.AccessibleName="原版规则分组，只读匹配量";layout.Controls.Add(tree,0,1);
   details.Text="查看规则与已保留路径";details.Size=new Size(200,32);details.Enabled=false;var actions=new FlowLayoutPanel{Dock=DockStyle.Fill};actions.Controls.Add(details);actions.Controls.Add(new Label{Text="分类量可能重叠；这里只对照占用，不提供清理。",AutoSize=true,Margin=new Padding(8,10,0,0)});layout.Controls.Add(actions,0,2);
   tree.AfterSelect+=delegate{details.Enabled=tree.SelectedNode!=null&&tree.SelectedNode.Tag is FolderUsage;};details.Click+=delegate{var folder=tree.SelectedNode==null?null:tree.SelectedNode.Tag as FolderUsage;if(folder==null||report==null)return;using(var dialog=CreateDetails(folder))dialog.ShowDialog(this);};
  }
  internal static string GroupName(CommunityEntry entry){
   if(entry==null)return "Other";List<string> value;
   if(entry.Directives.TryGetValue("Section",out value)&&value.Count>0&&!String.IsNullOrWhiteSpace(value[0]))return value[0];
   if(entry.Directives.TryGetValue("LangSecRef",out value)&&value.Count>0){switch(value[0]){case "3025":return "Windows";case "3024":return "Utilities";case "3021":return "Applications";case "3023":return "Multimedia";case "3022":return "Internet";case "3006":return "Edge Chromium";case "3029":return "Google Chrome";case "3026":return "Mozilla Firefox";default:return "LangSecRef "+value[0];}}
   return "Other";
  }
  internal void LoadReport(ScanReport value){
   report=value;tree.BeginUpdate();try{tree.Nodes.Clear();details.Enabled=false;if(value==null){summary.Text="尚未扫描";return;}
    summary.Text=(value.Cancelled?"已取消，结果不完整":value.Incomplete?"扫描结束，存在未知或未检查项":"扫描完成")+" · "+value.Folders.Count.ToString("N0")+" 个有匹配的规则 · 全局去重 "+Display.Size(value.LogicalBytes)+"\r\n展开分组查看明细。分组标题为各规则量之和，不能作为可释放空间，也不能相加到全局总量。";
    var grouped=value.Folders.GroupBy(folder=>{CommunityEntry definition;definitions.TryGetValue(folder.Path,out definition);return GroupName(definition);}).OrderByDescending(group=>group.Sum(f=>f.Bytes));
    foreach(var group in grouped){var parent=new TreeNode(group.Key+"  ("+Display.Size(group.Sum(f=>f.Bytes))+"，规则匹配量)");foreach(var folder in group.OrderByDescending(f=>f.Bytes)){string title=folder.Path.EndsWith(" *",StringComparison.Ordinal)?folder.Path.Substring(0,folder.Path.Length-2):folder.Path;parent.Nodes.Add(new TreeNode(title+"  ("+Display.Size(folder.Bytes)+")"){Tag=folder});}tree.Nodes.Add(parent);}
   }finally{tree.EndUpdate();}
  }
  internal Form CreateDetails(FolderUsage folder){
   if(report==null||folder==null)throw new InvalidOperationException("没有可查看的规则。");CommunityEntry definition;definitions.TryGetValue(folder.Path,out definition);var paths=report.Files.Where(f=>String.Equals(f.Category,folder.Path,StringComparison.OrdinalIgnoreCase)).ToList();
   var form=new Form{Text="规则明细 · "+folder.Path,Font=Font,Size=new Size(1050,700),MinimumSize=new Size(800,560),StartPosition=FormStartPosition.CenterParent};var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),RowCount=3,ColumnCount=1};layout.RowStyles.Add(new RowStyle(SizeType.Absolute,80));layout.RowStyles.Add(new RowStyle(SizeType.Percent,48));layout.RowStyles.Add(new RowStyle(SizeType.Percent,52));form.Controls.Add(layout);
   layout.Controls.Add(new Label{Dock=DockStyle.Fill,Text="规则匹配："+Display.Size(folder.Bytes)+" / "+folder.Files.ToString("N0")+" 个文件；当前报告保留该项路径 "+paths.Count.ToString("N0")+" 条。\r\n同一文件只保留首次匹配的分类；重叠规则可能没有单独路径。整份报告最多保留2000条，不是全部文件清单。\r\n以下为只读对照表达式；内置静态项与原始Winapp2定义分开标注，不代表删除建议。",TextAlign=ContentAlignment.MiddleLeft},0,0);
   var source=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false,Dock=DockStyle.Fill};source.Text=definition==null?"对照目录中没有该名称的定义（可能来自其他规则库）。":definition.Warning+"\r\n\r\n"+String.Join("\r\n",definition.FileKeys.Select((text,index)=>"FileKey"+(index+1)+"="+text).Concat(definition.ExcludeKeys.Select((text,index)=>"ExcludeKey"+(index+1)+"="+text)));layout.Controls.Add(source,0,1);
   var grid=new DataGridView{ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,Dock=DockStyle.Fill,VirtualMode=true,BackgroundColor=Color.White};grid.Columns.Add(new DataGridViewTextBoxColumn{HeaderText="已保留完整路径",Width=750});grid.Columns.Add(new DataGridViewTextBoxColumn{HeaderText="逻辑大小",Width=160});grid.CellValueNeeded+=delegate(object sender,DataGridViewCellValueEventArgs e){if(e.RowIndex>=paths.Count)return;e.Value=e.ColumnIndex==0?paths[e.RowIndex].Path:Display.Size(paths[e.RowIndex].Length);};grid.RowCount=paths.Count;layout.Controls.Add(grid,0,2);return form;
  }
 }
}
