using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ClearDisk {
 internal sealed class ChatCleanupDialog:Form {
  readonly SafetyPolicy policy;readonly ChatProduct product;
  readonly TextBox root=new TextBox();readonly ComboBox accounts=new ComboBox();
  readonly CheckBox images=new CheckBox{Text="图片",Checked=true},files=new CheckBox{Text="接收的文件",Checked=true},videos=new CheckBox{Text="视频",Checked=true};
  readonly NumericUpDown retain=new NumericUpDown{Minimum=0,Maximum=365000,Value=365};
  readonly Label status=new Label();
  readonly Button scan=new Button{Text="开始只读检查",Size=new Size(130,32)};
  string missingAccount;
  internal ChatScanOptions Options {get{return new ChatScanOptions{Product=product,Root=root.Text,SelectedAccount=accounts.SelectedIndex>0?Convert.ToString(accounts.SelectedItem):null,Images=images.Checked,Files=files.Checked,Videos=videos.Checked,RetentionDays=(int)retain.Value};}}
  internal ChatCleanupDialog(SafetyPolicy safety,ChatProduct selectedProduct){
   if(safety==null)throw new ArgumentNullException("safety");if(selectedProduct!=ChatProduct.WeChat4&&selectedProduct!=ChatProduct.QQGroup2)throw new ArgumentException("未知聊天产品。");policy=safety;product=selectedProduct;
   Text=product==ChatProduct.WeChat4?"微信 4 附件检查":"QQ 补清 · Group2 图片检查";Font=new Font("Microsoft YaHei UI",9);ClientSize=new Size(720,435);MinimumSize=Size;StartPosition=FormStartPosition.CenterParent;MinimizeBox=false;MaximizeBox=false;
   var table=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=3,RowCount=7};table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,112));table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));Controls.Add(table);
   foreach(int height in new[]{46,46,43,43,65,95,45})table.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
   LabelFor(table,"数据目录",0);root.Text=ChatInventoryScanner.DefaultRoot(policy,product);root.Dock=DockStyle.Fill;root.Margin=new Padding(0,8,8,0);table.Controls.Add(root,1,0);
   var browse=new Button{Text="自定义目录…",Dock=DockStyle.Fill,Margin=new Padding(0,4,0,5)};table.Controls.Add(browse,2,0);
   browse.Click+=delegate{using(var picker=new FolderBrowserDialog{Description=product==ChatProduct.WeChat4?"选择 xwechat_files 或微信账号目录":"选择 Tencent Files 或纯数字 QQ 账号目录",SelectedPath=root.Text,ShowNewFolderButton=false})if(picker.ShowDialog(this)==DialogResult.OK){root.Text=picker.SelectedPath;ReloadAccounts();}};
   LabelFor(table,"选择账号",1);accounts.DropDownStyle=ComboBoxStyle.DropDownList;accounts.Dock=DockStyle.Fill;accounts.Margin=new Padding(0,8,8,0);table.Controls.Add(accounts,1,1);
   var reload=new Button{Text="重新发现账号",Dock=DockStyle.Fill,Margin=new Padding(0,4,0,5)};reload.Click+=delegate{ReloadAccounts();};table.Controls.Add(reload,2,1);
   LabelFor(table,"附件类型",2);var kinds=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(0,6,0,0)};foreach(var checkbox in new[]{images,files,videos}){checkbox.AutoSize=true;kinds.Controls.Add(checkbox);}table.Controls.Add(kinds,1,2);table.SetColumnSpan(kinds,2);
   if(product==ChatProduct.QQGroup2){files.Checked=videos.Checked=false;files.Enabled=videos.Enabled=false;images.Enabled=false;retain.Value=0;}
   LabelFor(table,"保留最近天数",3);retain.Dock=DockStyle.Left;retain.Width=130;retain.Margin=new Padding(0,6,0,0);table.Controls.Add(retain,1,3);
   var retentionNote=new Label{Text="0 表示不保留过去的附件；按 UTC 文件最后修改时间筛选，不代表实际聊天创建时间。",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};table.Controls.Add(retentionNote,0,4);table.SetColumnSpan(retentionNote,3);
   status.Dock=DockStyle.Fill;status.ForeColor=Color.FromArgb(92,66,32);table.Controls.Add(status,0,5);table.SetColumnSpan(status,3);
   var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};var cancel=new Button{Text="取消",DialogResult=DialogResult.Cancel,Size=new Size(90,32)};buttons.Controls.Add(scan);buttons.Controls.Add(cancel);table.Controls.Add(buttons,0,6);table.SetColumnSpan(buttons,3);AcceptButton=scan;CancelButton=cancel;
   root.Leave+=delegate{ReloadAccounts();};
   accounts.SelectedIndexChanged+=delegate{scan.Enabled=accounts.SelectedIndex>=0&&(accounts.SelectedIndex==0||!String.Equals(Convert.ToString(accounts.SelectedItem),missingAccount,StringComparison.OrdinalIgnoreCase));};
   scan.Click+=delegate{
    if(accounts.SelectedIndex>0&&String.Equals(Convert.ToString(accounts.SelectedItem),missingAccount,StringComparison.OrdinalIgnoreCase))return;
    if(product==ChatProduct.WeChat4&&!images.Checked&&!files.Checked&&!videos.Checked){MessageBox.Show(this,"请至少选择一种附件类型。",Text);return;}
    string normalized;try{normalized=NativeFiles.Normalize(root.Text);}catch(Exception e){if(!ScanWalker.IsFileError(e))throw;MessageBox.Show(this,"仅支持当前用户 C 盘中的普通聊天数据目录。",Text);return;}
    if(!SafetyPolicy.IsWithin(normalized,policy.ProfileRoot)||!policy.ValidateInspectionRoot(normalized)||policy.IsProtected(normalized)){MessageBox.Show(this,"该自定义目录不在可用范围内。请选择当前用户 C 盘中的普通目录；项目、运行环境、系统、链接及离线目录不可用。",Text);return;}
    root.Text=normalized;DialogResult=DialogResult.OK;
   };
   ReloadAccounts();
  }
  static void LabelFor(TableLayoutPanel panel,string text,int row){panel.Controls.Add(new Label{Text=text,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,row);}
  void ReloadAccounts(){
   string selected=accounts.SelectedIndex>0?Convert.ToString(accounts.SelectedItem):null;
   string warning;var found=new ChatInventoryScanner(policy).DiscoverAccounts(product,root.Text,CancellationToken.None,out warning);
   string available=selected==null?null:found.Find(id=>String.Equals(id,selected,StringComparison.OrdinalIgnoreCase));missingAccount=selected!=null&&available==null?selected:null;
   accounts.BeginUpdate();try{accounts.Items.Clear();accounts.Items.Add("全部账号");foreach(string id in found)accounts.Items.Add(id);if(missingAccount!=null)accounts.Items.Add(missingAccount);accounts.SelectedIndex=selected!=null?accounts.Items.IndexOf(available??selected):0;}finally{accounts.EndUpdate();}
   string note=product==ChatProduct.WeChat4?"仅支持微信 4 的 msg 附件目录。未知和加密 .dat 格式仅查看，不解密。":"先使用 QQ 自带清理；本页仅检查数字账号的 Image\\Group2 图片。";
   status.Text=note+"\r\n不读取消息或数据库。结果默认不选，可手动选择符合既有策略的文件移入可恢复隔离区。\r\n"+(missingAccount!=null?"原选择账号 "+missingAccount+" 未发现，请明确选择可用账号或全部账号。":String.IsNullOrEmpty(warning)?"发现 "+found.Count+" 个普通账号。账号发现只检查浅层目录（最多 1000 项工作量）。":warning);
  }
 }
}
