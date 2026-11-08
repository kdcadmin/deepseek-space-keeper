using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClearDisk {
 internal static class FishPainter {
  internal static readonly Color Blue=Color.FromArgb(31,79,184);
  internal static readonly Color Deep=Color.FromArgb(23,58,143);
  internal static readonly Color Ink=Color.FromArgb(23,58,143);
  internal static readonly Color Red=Color.FromArgb(226,62,74);
  internal static readonly Color Well=Color.FromArgb(228,238,252);
  static Icon icon;
  static Bitmap iconBitmap;
  internal static Icon Icon { get { if(icon==null){ iconBitmap=new Bitmap(32,32); using(var g=Graphics.FromImage(iconBitmap)){ g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(Well); var face=PetFrames.Still; if(face!=null) g.DrawImage(face,new Rectangle(1,1,30,30)); else Draw(g,new Rectangle(1,2,30,28),0,false,false); } icon=Icon.FromHandle(iconBitmap.GetHicon()); } return icon; } }
  internal static void Draw(Graphics g,Rectangle box,int phase,bool searching,bool eating) {
   g.SmoothingMode=SmoothingMode.AntiAlias;
   float bob=(float)Math.Sin(phase/3.0)*(searching?5f:2f);
   float look=searching?(float)Math.Sin(phase/2.2)*4f:0f;
   var body=new RectangleF(box.X+box.Width*0.22f,box.Y+box.Height*0.30f+bob,box.Width*0.58f,box.Height*0.46f);
   using(var shadow=new SolidBrush(Color.FromArgb(40,37,99,235))) g.FillEllipse(shadow,body.X+body.Width*0.08f,box.Bottom-box.Height*0.12f,body.Width*0.84f,box.Height*0.08f);
   PointF[] tail=new[]{
    new PointF(body.X+body.Width*0.08f,body.Y+body.Height*0.35f),
    new PointF(box.X+box.Width*0.08f,body.Y+body.Height*0.02f+(searching?(float)Math.Sin(phase)*3f:0f)),
    new PointF(box.X+box.Width*0.05f,body.Bottom-body.Height*0.05f)
   };
   using(var tailBrush=new SolidBrush(Deep)) g.FillPolygon(tailBrush,tail);
   using(var fin=new SolidBrush(Color.FromArgb(96,165,250))) g.FillEllipse(fin,body.X+body.Width*0.28f,body.Y-body.Height*0.16f,body.Width*0.28f,body.Height*0.34f);
   using(var blue=new SolidBrush(Blue)) g.FillEllipse(blue,body);
   using(var belly=new SolidBrush(Color.White)) g.FillEllipse(belly,body.X+body.Width*0.16f,body.Y+body.Height*0.42f,body.Width*0.62f,body.Height*0.46f);
   float eyeX=body.Right-body.Width*0.28f+look,eyeY=body.Y+body.Height*0.34f;
   float eye=Math.Max(4f,box.Width*0.07f);
   using(var white=new SolidBrush(Color.White)) g.FillEllipse(white,eyeX,eyeY,eye,eye);
   using(var pupil=new SolidBrush(Ink)) g.FillEllipse(pupil,eyeX+eye*0.38f+(look>0?1f:0f),eyeY+eye*0.32f,eye*0.42f,eye*0.42f);
   using(var blush=new SolidBrush(Color.FromArgb(80,255,170,190))) g.FillEllipse(blush,eyeX-eye*0.2f,eyeY+eye*0.9f,eye*0.7f,eye*0.4f);
   using(var mouth=new Pen(Ink,Math.Max(1.4f,box.Width*0.025f))) {
    mouth.StartCap=LineCap.Round;mouth.EndCap=LineCap.Round;
    if(eating&&phase%8<4) g.DrawArc(mouth,body.Right-body.Width*0.28f,body.Y+body.Height*0.48f,body.Width*0.16f,body.Height*0.22f,20,140);
    else g.DrawArc(mouth,body.Right-body.Width*0.26f,body.Y+body.Height*0.52f,body.Width*0.14f,body.Height*0.12f,20,140);
   }
   if(searching) {
    float spin=(float)(phase*0.45);
    for(int i=0;i<3;i++) {
     double a=spin+i*2.1;
     float x=body.X+body.Width*0.2f+(float)Math.Cos(a)*body.Width*0.55f;
     float y=body.Y+body.Height*0.1f+(float)Math.Sin(a)*body.Height*0.55f;
     using(var scrap=new SolidBrush(i==2?Color.FromArgb(148,163,184):Color.FromArgb(100,116,139))) g.FillRectangle(scrap,x,y,box.Width*0.06f,box.Height*0.05f);
    }
   }
   if(eating) {
    var bowl=new RectangleF(body.Right-body.Width*0.08f,body.Bottom-body.Height*0.34f,body.Width*0.34f,body.Height*0.28f);
    using(var rim=new SolidBrush(Color.FromArgb(191,219,254))) g.FillEllipse(rim,bowl.X,bowl.Y,bowl.Width,bowl.Height*0.55f);
    using(var rice=new SolidBrush(Color.White)) {
     g.FillEllipse(rice,bowl.X+bowl.Width*0.08f,bowl.Y+bowl.Height*0.08f,bowl.Width*0.84f,bowl.Height*0.42f);
     g.FillEllipse(rice,bowl.X+bowl.Width*0.22f,bowl.Y-bowl.Height*0.08f,bowl.Width*0.16f,bowl.Height*0.16f);
     g.FillEllipse(rice,bowl.X+bowl.Width*0.42f,bowl.Y-bowl.Height*0.02f,bowl.Width*0.16f,bowl.Height*0.16f);
     g.FillEllipse(rice,bowl.X+bowl.Width*0.60f,bowl.Y-bowl.Height*0.08f,bowl.Width*0.16f,bowl.Height*0.16f);
    }
    using(var bowlBrush=new SolidBrush(Blue)) g.FillPie(bowlBrush,bowl.X,bowl.Y+bowl.Height*0.08f,bowl.Width,bowl.Height,0,180);
   }
  }
 }
 internal static class PetFrames {
  static readonly Dictionary<string,Bitmap[]> cache=new Dictionary<string,Bitmap[]>(StringComparer.Ordinal);
  static Bitmap still;
  internal static Bitmap Still { get { if(still==null){ string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"pet","idle","001.png"); if(File.Exists(path)) using(var raw=new Bitmap(path)) still=new Bitmap(raw); } return still; } }
  internal static Bitmap[] Get(string name){ Bitmap[] frames; if(cache.TryGetValue(name,out frames)) return frames; frames=Load(name); cache[name]=frames; return frames; }
  static Bitmap[] Load(string name){
   string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"pet",name);
   if(!Directory.Exists(dir)) return new Bitmap[0];
   var list=new List<Bitmap>();
   foreach(string file in Directory.GetFiles(dir,"*.png").OrderBy(x=>x,StringComparer.Ordinal)) list.Add(Key(file));
   return list.ToArray();
  }
  static Bitmap Key(string file){
   using(var raw=new Bitmap(file)){
    var bmp=new Bitmap(240,240,PixelFormat.Format32bppArgb);
    using(var g=Graphics.FromImage(bmp)){g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(raw,0,0,240,240);}
    var data=bmp.LockBits(new Rectangle(0,0,bmp.Width,bmp.Height),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
    int bytes=Math.Abs(data.Stride)*bmp.Height; var buf=new byte[bytes];
    Marshal.Copy(data.Scan0,buf,0,bytes);
    for(int i=0;i<bytes;i+=4){int b=buf[i],gc=buf[i+1],r=buf[i+2],a=buf[i+3];bool green=gc>120&&gc>r+24&&gc>b+24;if(a<32||green){buf[i]=buf[i+1]=buf[i+2]=buf[i+3]=0;}else{buf[i]=(byte)(b*a/255);buf[i+1]=(byte)(gc*a/255);buf[i+2]=(byte)(r*a/255);}}
    Marshal.Copy(buf,0,data.Scan0,bytes);bmp.UnlockBits(data);return bmp;
   }
  }
 }
 internal sealed class DesktopPetForm : Form {
  const int WS_EX_LAYERED=0x80000,ULW_ALPHA=2,WM_NCHITTEST=0x84,HTTRANSPARENT=-1;
  readonly MainForm main;
  readonly PetChrome chrome;
  readonly PetShade shade;
  readonly Timer timer=new Timer{Interval=125};
  readonly Bitmap[] hit=new Bitmap[1];
  readonly Dictionary<Bitmap,Rectangle> heads=new Dictionary<Bitmap,Rectangle>();
  readonly NotifyIcon tray=new NotifyIcon();
  string clipName="idle";
  int frame;
  Point grab;
  bool dragging,moved,menuOpen,shutting;
  internal DesktopPetForm(MainForm owner) {
   main=owner;Name="desktopPet";Text="大肥鱼";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;Size=new Size(240,240);
   chrome=new PetChrome(this);shade=new PetShade(this);
   timer.Tick+=delegate{var frames=PetFrames.Get(clipName);if(frames.Length>0){frame=(frame+1)%frames.Length;ShowFrame(frames[frame]);}if(main.IsBusy&&menuOpen)Dismiss();};
   LocationChanged+=delegate{if(hit[0]!=null&&IsHandleCreated)Present(hit[0]);PlaceBubble();};
   SetPose(false,false);
   var area=Screen.PrimaryScreen.WorkingArea;Location=new Point(area.Right-Width-36,area.Bottom-Height-120);
   var menu=new ContextMenuStrip();
   var open=new ToolStripMenuItem("打开主界面");open.Click+=delegate{main.ShowFromPet();};
   var quit=new ToolStripMenuItem("完全退出");quit.Click+=delegate{main.ExitCompletely();};
   menu.Items.Add(open);menu.Items.Add(quit);
   tray.Icon=FishPainter.Icon;tray.Text="大肥鱼空间管家";tray.Visible=true;tray.ContextMenuStrip=menu;tray.DoubleClick+=delegate{main.ShowFromPet();};
   PlaceBubble();
   timer.Start();FormClosed+=delegate{timer.Dispose();tray.Visible=false;tray.Dispose();if(!chrome.IsDisposed)chrome.Close();if(shade!=null&&!shade.IsDisposed)shade.Close();};
  }
  internal void Shutdown(){if(shutting||IsDisposed)return;shutting=true;tray.Visible=false;Close();}
  void PlaceBubble(){if(chrome==null||chrome.IsDisposed)return;chrome.Follow(Bounds,HeadBox());}
  Rectangle HeadBox(){var bmp=hit[0];Rectangle box;if(bmp==null)return new Rectangle(90,36,60,40);if(heads.TryGetValue(bmp,out box))return box;box=MeasureHead(bmp);heads[bmp]=box;return box;}
  static Rectangle MeasureHead(Bitmap bmp){
   var data=bmp.LockBits(new Rectangle(0,0,bmp.Width,bmp.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
   int minX=bmp.Width,minY=bmp.Height,maxX=-1,maxY=-1,stride=data.Stride,bytes=Math.Abs(data.Stride)*bmp.Height;var buf=new byte[bytes];
   Marshal.Copy(data.Scan0,buf,0,bytes);bmp.UnlockBits(data);
   for(int y=0;y<bmp.Height;y++){int row=y*stride;for(int x=0;x<bmp.Width;x++)if(buf[row+x*4+3]>24){if(x<minX)minX=x;if(y<minY)minY=y;if(x>maxX)maxX=x;if(y>maxY)maxY=y;}}
   if(maxX<0)return new Rectangle(40,40,80,90);
   int headH=Math.Max(36,(maxY-minY)*52/100);
   return new Rectangle(minX,minY+headH/5,Math.Max(24,(maxX-minX)/2),headH);
  }
  internal void Attach(){}
  protected override CreateParams CreateParams { get { var cp=base.CreateParams; cp.ExStyle|=WS_EX_LAYERED; return cp; } }
  internal void SetPose(bool working,bool cleaning){string next=cleaning?"eat":working?"search":"idle";if(next!=clipName){clipName=next;frame=0;}var frames=PetFrames.Get(clipName);if(frames.Length>0)ShowFrame(frames[frame%frames.Length]);if(working)Dismiss();}
  internal async void Choose(int kind){Dismiss();if(kind>=0&&!main.IsBusy)await main.PetScan(kind);}
  internal void SelectAll(){if(!main.IsBusy)main.PetSelectAll();}
  internal void CancelPick(){if(!main.IsBusy)main.PetClear();Dismiss();}
  internal async Task Clean(){if(main.IsBusy)return;Dismiss();await main.PetClean();}
  internal void ShowActions(){if(main.IsBusy)return;menuOpen=true;chrome.ShowMode(false);OpenBubble();}
  internal void ToggleMenu(){if(main.IsBusy)return;if(chrome.Visible){Dismiss();return;}menuOpen=true;chrome.ShowMode(true);OpenBubble();}
  internal void Dismiss(){menuOpen=false;if(chrome!=null&&!chrome.IsDisposed)chrome.HideBubble();if(shade!=null&&!shade.IsDisposed)shade.Hide();}
  void OpenBubble(){var screen=Screen.FromControl(this).Bounds;shade.Bounds=screen;if(!shade.Visible)shade.Show();if(!chrome.Visible)chrome.Show();PlaceBubble();BringToFront();chrome.BringToFront();}
  void ShowFrame(Bitmap bmp){hit[0]=bmp;if(!IsHandleCreated)CreateHandle();Present(bmp);}
  void Present(Bitmap bmp){
   IntPtr screen=GetDC(IntPtr.Zero);IntPtr mem=CreateCompatibleDC(screen);IntPtr bits;IntPtr dib=Dib(bmp,out bits);IntPtr old=SelectObject(mem,dib);
   var size=new SIZE{cx=bmp.Width,cy=bmp.Height};var dst=new POINT{X=Left,Y=Top};var src=new POINT();var blend=new BLENDFUNCTION{BlendOp=0,SourceConstantAlpha=255,AlphaFormat=1};
   UpdateLayeredWindow(Handle,screen,ref dst,ref size,mem,ref src,0,ref blend,ULW_ALPHA);
   SelectObject(mem,old);DeleteObject(dib);DeleteDC(mem);ReleaseDC(IntPtr.Zero,screen);
  }
  bool Opaque(Point client){var bmp=hit[0];if(bmp==null||client.X<0||client.Y<0||client.X>=bmp.Width||client.Y>=bmp.Height)return false;return bmp.GetPixel(client.X,client.Y).A>24;}
  protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Right){if(tray.ContextMenuStrip!=null)tray.ContextMenuStrip.Show(Cursor.Position);return;}if(e.Button!=MouseButtons.Left||!Opaque(e.Location))return;grab=MousePosition;dragging=true;moved=false;base.OnMouseDown(e);}
  protected override void OnMouseMove(MouseEventArgs e){if(!dragging||e.Button!=MouseButtons.Left){base.OnMouseMove(e);return;}Point now=MousePosition;if(Math.Abs(now.X-grab.X)+Math.Abs(now.Y-grab.Y)>4)moved=true;if(moved){Location=new Point(Left+now.X-grab.X,Top+now.Y-grab.Y);grab=now;}base.OnMouseMove(e);}
  protected override void OnMouseUp(MouseEventArgs e){if(e.Button==MouseButtons.Left){dragging=false;if(!moved)ToggleMenu();}base.OnMouseUp(e);}
  protected override void WndProc(ref Message m){if(m.Msg==WM_NCHITTEST){Point p=PointToClient(Cursor.Position);if(!Opaque(p)){m.Result=(IntPtr)HTTRANSPARENT;return;}}base.WndProc(ref m);}
  static IntPtr Dib(Bitmap bmp,out IntPtr bits){
   var info=new BITMAPINFO();info.bmiHeader.biSize=40;info.bmiHeader.biWidth=bmp.Width;info.bmiHeader.biHeight=-bmp.Height;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;info.bmiHeader.biCompression=0;
   IntPtr dib=CreateDIBSection(IntPtr.Zero,ref info,0,out bits,IntPtr.Zero,0);
   var data=bmp.LockBits(new Rectangle(0,0,bmp.Width,bmp.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
   int bytes=Math.Abs(data.Stride)*bmp.Height;var buf=new byte[bytes];Marshal.Copy(data.Scan0,buf,0,bytes);bmp.UnlockBits(data);Marshal.Copy(buf,0,bits,bytes);return dib;
  }
  [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd,IntPtr hdcDst,ref POINT pptDst,ref SIZE psize,IntPtr hdcSrc,ref POINT pptSrc,int crKey,ref BLENDFUNCTION pblend,int dwFlags);
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd,IntPtr hdc);
  [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
  [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
  [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc,IntPtr obj);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
  [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc,ref BITMAPINFO pbmi,uint usage,out IntPtr bits,IntPtr section,uint offset);
  [StructLayout(LayoutKind.Sequential)] struct POINT{public int X,Y;}
  [StructLayout(LayoutKind.Sequential)] struct SIZE{public int cx,cy;}
  [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION{public byte BlendOp,BlendFlags,SourceConstantAlpha,AlphaFormat;}
  [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER{public int biSize,biWidth,biHeight;public short biPlanes,biBitCount;public int biCompression,biSizeImage,biXPelsPerMeter,biYPelsPerMeter,biClrUsed,biClrImportant;}
  [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO{public BITMAPINFOHEADER bmiHeader;}
 }
 internal sealed class PetShade : Form {
  readonly DesktopPetForm pet;
  internal PetShade(DesktopPetForm owner){pet=owner;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;BackColor=Color.White;Opacity=0.01;Cursor=Cursors.Default;}
  protected override void OnMouseDown(MouseEventArgs e){pet.Dismiss();base.OnMouseDown(e);}
 }
 internal sealed class PetChrome : Form {
  readonly DesktopPetForm pet;
  readonly Button[] choices;
  readonly Button selectAll=new Button(),cancel=new Button(),confirm=new Button();
  bool picking;
  int hover=-1;
  internal PetChrome(DesktopPetForm owner){
   pet=owner;Name="petChrome";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;BackColor=Color.White;Font=new Font("Microsoft YaHei UI",9F);Visible=false;
   choices=new[]{Choice("C盘清理",0),Choice("垃圾清理",1),Choice("AI 缓存",2)};
   selectAll.Name="petSelectAllButton";cancel.Name="petCancelButton";confirm.Name="petConfirmButton";
   Quiet(selectAll,"全选");Quiet(cancel,"取消");Quiet(confirm,"确认并清理");confirm.ForeColor=Color.White;confirm.BackColor=FishPainter.Red;confirm.FlatAppearance.MouseOverBackColor=Color.FromArgb(196,42,54);confirm.Font=new Font("Microsoft YaHei UI",9F,FontStyle.Bold);
   selectAll.Click+=delegate{if(selectAll.Enabled)pet.SelectAll();};
   cancel.Click+=delegate{pet.CancelPick();};
   confirm.Click+=async delegate{if(confirm.Enabled)await pet.Clean();};
   foreach(var button in choices){button.Visible=false;Controls.Add(button);}
   selectAll.Visible=cancel.Visible=confirm.Visible=false;
   Controls.Add(selectAll);Controls.Add(cancel);Controls.Add(confirm);
   LocationChanged+=delegate{if(IsHandleCreated&&Visible)PresentBubble();};
  }
  protected override CreateParams CreateParams { get { var cp=base.CreateParams; cp.ExStyle|=0x80000; return cp; } }
  internal void HideBubble(){Hide();}
  internal void ShowMode(bool choose){picking=choose;hover=-1;LayoutBubble();Show();PresentBubble();}
  internal void Follow(Rectangle sprite,Rectangle head){var tip=Bubble(1);int ax=sprite.Left+head.X+10,ay=sprite.Top+head.Y+2;Left=ax-(tip.X+tip.Width/2);Top=ay-(tip.Y+tip.Height/2);var screen=Screen.FromPoint(new Point(Math.Max(0,ax),Math.Max(0,ay))).WorkingArea;if(Left<screen.Left+4)Left=screen.Left+4;if(Left+Width>screen.Right-4)Left=screen.Right-4-Width;if(Top<screen.Top+4)Top=screen.Top+4;}
  void LayoutBubble(){
   Size=picking?new Size(206,186):new Size(228,154);
   var body=BubbleBody();
   for(int i=0;i<choices.Length;i++){var row=RowRect(i);choices[i].SetBounds(row.X,row.Y,row.Width,row.Height);}
   selectAll.Visible=cancel.Visible=confirm.Visible=false;
   if(!picking){selectAll.SetBounds(body.X+14,body.Y+18,48,30);cancel.SetBounds(body.X+66,body.Y+18,48,30);confirm.SetBounds(body.X+118,body.Y+16,72,32);using(var pill=Round(new Rectangle(0,0,confirm.Width,confirm.Height),16))confirm.Region=new Region(pill);}
   Region=null;
   PresentBubble();
  }
  Rectangle RowRect(int index){var body=BubbleBody();int w=Math.Min(118,body.Width-84);int x=body.X+(body.Width-w)/2;return new Rectangle(x,body.Y+22+index*32,w,28);}
  static Rectangle Outer(Rectangle inner){inner.Inflate(6,6);return inner;}
  static void Union(Region region,Rectangle rect){rect.Inflate(2,2);using(var path=new GraphicsPath()){path.AddEllipse(rect);region.Union(path);}}
  static void PaintWrapped(Graphics g,Rectangle inner){using(var blue=new SolidBrush(Color.FromArgb(27,62,150)))g.FillEllipse(blue,Outer(inner));using(var white=new SolidBrush(Color.White))g.FillEllipse(white,inner);}
  Rectangle BubbleBody(){return new Rectangle(8,4,Width-16,Height-58);}
  Rectangle Bubble(int index){int x=Width*56/100;return index==0?new Rectangle(x,Height-64,34,34):new Rectangle(x+18,Height-36,16,16);}
  Button Choice(string text,int kind){var button=new Button{Text=text,Name="petChoice"+kind};Quiet(button,text);button.Click+=delegate{pet.Choose(kind);};return button;}
  static void Quiet(Button b,string text){b.Text=text;b.FlatStyle=FlatStyle.Flat;b.UseVisualStyleBackColor=false;b.FlatAppearance.BorderSize=0;b.FlatAppearance.MouseOverBackColor=Color.FromArgb(232,240,255);b.BackColor=Color.White;b.ForeColor=Color.FromArgb(27,62,150);b.Cursor=Cursors.Hand;b.Font=new Font("Microsoft YaHei UI",11F,FontStyle.Bold);}
  static GraphicsPath Round(Rectangle bounds,int radius){int d=radius*2;var path=new GraphicsPath();path.AddArc(bounds.X,bounds.Y,d,d,180,90);path.AddArc(bounds.Right-d,bounds.Y,d,d,270,90);path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90);path.AddArc(bounds.X,bounds.Bottom-d,d,d,90,90);path.CloseFigure();return path;}
  void PresentBubble(){
   if(!IsHandleCreated||Width<8||Height<8)return;
   using(var bmp=new Bitmap(Width,Height,PixelFormat.Format32bppArgb)){
    using(var g=Graphics.FromImage(bmp)){
     g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.Half;g.Clear(Color.Transparent);
     var body=BubbleBody();
     using(var shadow=new SolidBrush(Color.FromArgb(36,27,62,150))){var drop=Outer(body);drop.Offset(0,4);g.FillEllipse(shadow,drop);}
     PaintWrapped(g,body);PaintWrapped(g,Bubble(0));PaintWrapped(g,Bubble(1));
     if(picking){
      string[] labels={"C盘清理","垃圾清理","AI 缓存"};
      using(var font=new Font("Microsoft YaHei UI",11F,FontStyle.Bold))
      using(var wash=new SolidBrush(Color.FromArgb(232,240,255))){
       var flags=TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix;
       for(int i=0;i<labels.Length;i++){var row=RowRect(i);if(hover==i)g.FillRectangle(wash,row);TextRenderer.DrawText(g,labels[i],font,row,Color.FromArgb(27,62,150),Color.White,flags);}
      }
     } else {
      using(var font=new Font("Microsoft YaHei UI",9F,FontStyle.Bold)){
       var flags=TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix;
       TextRenderer.DrawText(g,"全选",font,selectAll.Bounds,Color.FromArgb(27,62,150),Color.White,flags);
       TextRenderer.DrawText(g,"取消",font,cancel.Bounds,Color.FromArgb(27,62,150),Color.White,flags);
       using(var red=new SolidBrush(FishPainter.Red))
       using(var path=Round(confirm.Bounds,16)) g.FillPath(red,path);
       TextRenderer.DrawText(g,"确认并清理",font,confirm.Bounds,Color.White,FishPainter.Red,flags);
      }
     }
    }
    if(picking)for(int i=0;i<3;i++)KeepOpaque(bmp,RowRect(i));else{KeepOpaque(bmp,selectAll.Bounds);KeepOpaque(bmp,cancel.Bounds);KeepOpaque(bmp,confirm.Bounds);}
    Premultiply(bmp);Blit(bmp);
   }
  }
  static void KeepOpaque(Bitmap bmp,Rectangle area){
   int x0=Math.Max(0,area.X),y0=Math.Max(0,area.Y),x1=Math.Min(bmp.Width,area.Right),y1=Math.Min(bmp.Height,area.Bottom);
   for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++){var c=bmp.GetPixel(x,y);if(c.A<255)bmp.SetPixel(x,y,Color.FromArgb(255,c));}
  }
  static void Premultiply(Bitmap bmp){
   var data=bmp.LockBits(new Rectangle(0,0,bmp.Width,bmp.Height),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
   int bytes=Math.Abs(data.Stride)*bmp.Height;var buf=new byte[bytes];Marshal.Copy(data.Scan0,buf,0,bytes);
   for(int i=0;i<bytes;i+=4){int a=buf[i+3];buf[i]=(byte)(buf[i]*a/255);buf[i+1]=(byte)(buf[i+1]*a/255);buf[i+2]=(byte)(buf[i+2]*a/255);}
   Marshal.Copy(buf,0,data.Scan0,bytes);bmp.UnlockBits(data);
  }
  void Blit(Bitmap bmp){
   IntPtr screen=GetDC(IntPtr.Zero);IntPtr mem=CreateCompatibleDC(screen);IntPtr bits;IntPtr dib=Dib(bmp,out bits);IntPtr old=SelectObject(mem,dib);
   var size=new SIZE{cx=bmp.Width,cy=bmp.Height};var dst=new POINT{X=Left,Y=Top};var src=new POINT();var blend=new BLENDFUNCTION{BlendOp=0,SourceConstantAlpha=255,AlphaFormat=1};
   UpdateLayeredWindow(Handle,screen,ref dst,ref size,mem,ref src,0,ref blend,2);
   SelectObject(mem,old);DeleteObject(dib);DeleteDC(mem);ReleaseDC(IntPtr.Zero,screen);
  }
  static IntPtr Dib(Bitmap bmp,out IntPtr bits){
   var info=new BITMAPINFO();info.bmiHeader.biSize=40;info.bmiHeader.biWidth=bmp.Width;info.bmiHeader.biHeight=-bmp.Height;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;
   IntPtr dib=CreateDIBSection(IntPtr.Zero,ref info,0,out bits,IntPtr.Zero,0);
   var data=bmp.LockBits(new Rectangle(0,0,bmp.Width,bmp.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
   int bytes=Math.Abs(data.Stride)*bmp.Height;var buf=new byte[bytes];Marshal.Copy(data.Scan0,buf,0,bytes);bmp.UnlockBits(data);Marshal.Copy(buf,0,bits,bytes);return dib;
  }
  bool InEllipse(Rectangle ellipse,Point p){float rx=ellipse.Width/2f,ry=ellipse.Height/2f;if(rx<1||ry<1)return false;float dx=(p.X-ellipse.X-rx)/rx,dy=(p.Y-ellipse.Y-ry)/ry;return dx*dx+dy*dy<=1f;}
  protected override void OnMouseMove(MouseEventArgs e){int next=-1;if(picking)for(int i=0;i<3;i++)if(RowRect(i).Contains(e.Location))next=i;if(next!=hover){hover=next;Cursor=next>=0?Cursors.Hand:Cursors.Default;PresentBubble();}base.OnMouseMove(e);}
  protected override void OnMouseUp(MouseEventArgs e){
   if(e.Button!=MouseButtons.Left){base.OnMouseUp(e);return;}
   if(!InEllipse(Outer(BubbleBody()),e.Location)&&!InEllipse(Outer(Bubble(0)),e.Location)&&!InEllipse(Outer(Bubble(1)),e.Location)){pet.Dismiss();return;}
   if(picking){for(int i=0;i<3;i++)if(RowRect(i).Contains(e.Location)){pet.Choose(i);return;}}
   else if(selectAll.Bounds.Contains(e.Location))pet.SelectAll();
   else if(cancel.Bounds.Contains(e.Location))pet.CancelPick();
   else if(confirm.Bounds.Contains(e.Location))pet.Clean();
  }
  [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd,IntPtr hdcDst,ref POINT pptDst,ref SIZE psize,IntPtr hdcSrc,ref POINT pptSrc,int crKey,ref BLENDFUNCTION pblend,int dwFlags);
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd,IntPtr hdc);
  [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
  [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
  [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc,IntPtr obj);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
  [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc,ref BITMAPINFO pbmi,uint usage,out IntPtr bits,IntPtr section,uint offset);
  [StructLayout(LayoutKind.Sequential)] struct POINT{public int X,Y;}
  [StructLayout(LayoutKind.Sequential)] struct SIZE{public int cx,cy;}
  [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION{public byte BlendOp,BlendFlags,SourceConstantAlpha,AlphaFormat;}
  [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER{public int biSize,biWidth,biHeight;public short biPlanes,biBitCount;public int biCompression,biSizeImage,biXPelsPerMeter,biYPelsPerMeter,biClrUsed,biClrImportant;}
  [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO{public BITMAPINFOHEADER bmiHeader;}
 }
}
