using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ClearDisk {
 internal enum FileSearchKind { LargeFiles, Duplicates }

 internal sealed class FileSearchOptions {
  internal string SearchRoot { get; private set; }
  internal long MinimumBytes { get; private set; }
  internal FileSearchKind Kind { get; private set; }
  internal FileSearchOptions(string searchRoot,long minimumBytes,FileSearchKind kind) {
   SearchRoot=searchRoot;MinimumBytes=minimumBytes;Kind=kind;
  }
 }

 internal sealed class FileSearchDialog:Form {
  readonly SafetyPolicy policy;
  readonly FileSearchKind kind;
  readonly TextBox root=new TextBox();
  readonly NumericUpDown minimum=new NumericUpDown();
  const decimal MaximumMegabytes=1048576;

  internal FileSearchOptions Options {
   get { return new FileSearchOptions(ValidateSearchRoot(root.Text),MinimumBytesFromMegabytes(minimum.Value),kind); }
  }

  internal FileSearchDialog(SafetyPolicy safety,FileSearchKind searchKind) {
   if(safety==null)throw new ArgumentNullException("safety");
   if(searchKind!=FileSearchKind.LargeFiles&&searchKind!=FileSearchKind.Duplicates)throw new ArgumentOutOfRangeException("searchKind");
   policy=safety;kind=searchKind;
   Text=kind==FileSearchKind.LargeFiles?"大文件查找":"重复文件查找";
   Font=new Font("Microsoft YaHei UI",9);
   ClientSize=new Size(680,280);MinimumSize=Size;
   StartPosition=FormStartPosition.CenterParent;MinimizeBox=false;MaximizeBox=false;
   var panel=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=3,RowCount=4 };
   panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150));
   panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
   panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90));
   foreach(int height in new[]{44,44,104,44})panel.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
   Controls.Add(panel);
   panel.Controls.Add(new Label { Text="选择分区或者目录",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft },0,0);
   root.ReadOnly=true;root.Text=policy.ProfileRoot;root.Dock=DockStyle.Fill;root.Margin=new Padding(0,8,8,0);
   root.AccessibleName="选择分区或者目录";panel.Controls.Add(root,1,0);
   var browse=new Button { Text="选择…",Dock=DockStyle.Fill,Margin=new Padding(0,4,0,6) };panel.Controls.Add(browse,2,0);
   browse.Click+=delegate {
    using(var picker=new FolderBrowserDialog { Description=kind==FileSearchKind.Duplicates?"选择当前用户的普通个人目录（不含 AppData）":"选择 C 盘普通目录或分区，只读查找大文件",SelectedPath=root.Text,ShowNewFolderButton=false }) {
     if(picker.ShowDialog(this)!=DialogResult.OK)return;
     try { SelectRoot(picker.SelectedPath); }
     catch(Exception e) { if(!IsPathError(e))throw;MessageBox.Show(this,e.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Information); }
    }
   };
   panel.Controls.Add(new Label { Text="查找大于(MB)",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft },0,1);
   minimum.Minimum=0;minimum.Maximum=MaximumMegabytes;minimum.Value=kind==FileSearchKind.LargeFiles?100:1;
   minimum.Dock=DockStyle.Fill;minimum.Margin=new Padding(0,8,8,0);minimum.AccessibleName="查找大于(MB)";panel.Controls.Add(minimum,1,1);
   var note=new Label { Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Text="目录只能通过选择按钮更换。大小单位为 MB（1 MB = 1,048,576 字节）；0 表示至少 1 字节。\r\n"+
    (kind==FileSearchKind.Duplicates?"重复文件仅检查当前用户的普通目录，不含 AppData。":"大文件查找只读检查 C 盘；受保护目录由扫描器跳过。")+
    "\r\n扫描只生成结果；个人文件默认不选，人工核对后才可通过主界面预览隔离。" };
   panel.Controls.Add(note,0,2);panel.SetColumnSpan(note,3);
   var buttons=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft };
   var scan=new Button { Text="开始扫描",Size=new Size(110,32) };
   var cancel=new Button { Text="取消",DialogResult=DialogResult.Cancel,Size=new Size(90,32) };
   buttons.Controls.Add(scan);buttons.Controls.Add(cancel);panel.Controls.Add(buttons,0,3);panel.SetColumnSpan(buttons,3);
   AcceptButton=cancel;CancelButton=cancel;
   Shown+=delegate { cancel.Focus(); };
   scan.Click+=delegate {
    try { var options=Options;DialogResult=DialogResult.OK; }
    catch(Exception e) { if(!IsPathError(e))throw;MessageBox.Show(this,e.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Information); }
   };
  }

  internal void SelectRoot(string path) { root.Text=ValidateSearchRoot(path); }

  internal static long MinimumBytesFromMegabytes(decimal megabytes) {
   if(megabytes<0||megabytes>MaximumMegabytes||Decimal.Truncate(megabytes)!=megabytes)throw new ArgumentOutOfRangeException("megabytes");
   return Math.Max(1L,checked((long)megabytes*1048576L));
  }

  string ValidateSearchRoot(string path) {
   if(String.IsNullOrWhiteSpace(path)||!path.StartsWith(@"C:\",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("请选择 C 盘绝对目录。");
   foreach(string part in path.Substring(3).Split(new[]{'\\','/'}))
    if(part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal))throw new ArgumentException("请选择不含特殊结尾的普通目录。");
   string full=NativeFiles.Normalize(path);
   if(kind==FileSearchKind.Duplicates&&(!SafetyPolicy.IsWithin(full,policy.ProfileRoot)||SafetyPolicy.IsWithin(full,Path.Combine(policy.ProfileRoot,"AppData"))))
    throw new IOException("重复文件查找请选择当前用户的普通个人目录，不含 AppData。");
   try {
    // Pin every parent, including the selected directory, while checking its ordinary metadata.
    using(var parents=new NativeFiles.ParentLocks(Path.Combine(full,".file-search")))
    using(var handle=NativeFiles.Open(full,false,true)) {
     uint blocked=0x400u|0x1000u|0x40000u|(full.Length==3?0u:0x4u);
     var info=NativeFiles.Info(handle);
     if((info.Attributes&0x10u)==0||(info.Attributes&blocked)!=0)throw new IOException("请选择可访问的普通本地目录，不接受系统、链接或离线目录。");
     if(!String.Equals(NativeFiles.Final(handle),full,StringComparison.OrdinalIgnoreCase)||(full.Length!=3&&!policy.ValidateRoot(full)))throw new IOException("所选目录受到保护或路径已经变化。");
    }
   } catch(Win32Exception e) { throw new IOException("无法核验所选目录："+e.Message,e); }
   catch(UnauthorizedAccessException e) { throw new IOException("无法访问所选目录："+e.Message,e); }
   return full;
  }

  static bool IsPathError(Exception error) { return error is IOException||error is ArgumentException||error is NotSupportedException; }
 }
}
