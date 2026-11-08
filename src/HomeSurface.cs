using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ClearDisk {
 internal sealed class HomeSurface : Control {
  internal event EventHandler DriveChosen, JunkChosen, AiChosen;
  internal string SpaceText="正在读取 C 盘…";
  internal string Notice;
  internal double UsedRatio;
  readonly string[] titles={"C盘清理","垃圾清理","AI 缓存"};
  readonly string[] bodies={
   "系统和软件占了多少，先列出来。你确认之后才删除。",
   "临时文件，以及软件自己能重新生成的缓存。",
   "Codex、Cursor、DeepSeek Harness。只清缓存和日志，对话、登录、设置留下。"
  };
  readonly Color[] accents={Color.FromArgb(31,79,184),Color.FromArgb(31,79,184),Color.FromArgb(31,79,184)};
  int hover=-1;
  Rectangle[] cards=new Rectangle[3];
  readonly Font titleFont,cardFont,strongFont;
  internal static readonly Image Brand = LoadBrand();
  static Image LoadBrand() {
   Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("ClearDisk.AppIcon");
   if(stream==null) return null;
   using(stream) using(Image raw=Image.FromStream(stream)) return new Bitmap(raw);
  }
  public HomeSurface() {
   Name="homeSurface";
   SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
   BackColor=Color.White;
   Font=new Font("Microsoft YaHei UI",10F);
   titleFont=new Font(Font.FontFamily,22,FontStyle.Bold);
   cardFont=new Font(Font.FontFamily,18,FontStyle.Bold);
   strongFont=new Font(Font,FontStyle.Bold);
  }
  protected override void Dispose(bool disposing){if(disposing){titleFont.Dispose();cardFont.Dispose();strongFont.Dispose();}base.Dispose(disposing);}
  void Measure() {
   int pad=Math.Max(36,ClientSize.Width/18);
   int gap=18;
   int top=Math.Max(200,(ClientSize.Height-176)/2);
   int width=Math.Max(160,(ClientSize.Width-pad*2-gap*2)/3);
   int height=176;
   for(int i=0;i<3;i++) cards[i]=new Rectangle(pad+i*(width+gap),top,width,height);
  }
  protected override void OnPaint(PaintEventArgs e) {
   Measure();
   Graphics g=e.Graphics;
   g.SmoothingMode=SmoothingMode.AntiAlias;
   g.Clear(BackColor);
   var ink=Color.FromArgb(23,58,143);
   var muted=Color.FromArgb(120,136,160);
   int origin=Math.Max(36,ClientSize.Width/18);
   Portrait.Draw(g,new Rectangle(origin,22,68,68));
   int textX=origin+80;
   TextRenderer.DrawText(g,"大肥鱼空间管家",titleFont,new Rectangle(textX,28,640,48),ink,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);
   TextRenderer.DrawText(g,"两件事，加上 AI 缓存。扫描只列出结果，确认后才清理。",Font,new Rectangle(origin,96,860,22),muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);
   TextRenderer.DrawText(g,SpaceText,strongFont,new Rectangle(origin,122,720,24),ink,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);
   if(!String.IsNullOrEmpty(Notice)) TextRenderer.DrawText(g,Notice,Font,new Rectangle(origin,168,860,24),Color.FromArgb(31,79,184),TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
   var track=new Rectangle(origin,152,Math.Min(420,ClientSize.Width/3),8);
   using(var path=Round(track,4))
   using(var trackBrush=new SolidBrush(Color.FromArgb(226,234,246))) g.FillPath(trackBrush,path);
   int fillW=(int)Math.Round(track.Width*Math.Max(0,Math.Min(1,UsedRatio)));
   if(fillW>4) using(var path=Round(new Rectangle(track.X,track.Y,fillW,track.Height),4))
   using(var used=new SolidBrush(UsedRatio>=0.9?Color.FromArgb(226,62,74):Color.FromArgb(31,79,184))) g.FillPath(used,path);
   for(int i=0;i<3;i++) PaintCard(g,i,ink,muted);
  }
  void PaintCard(Graphics g,int index,Color ink,Color muted) {
   Rectangle card=cards[index];
   bool on=hover==index;
   using(var path=Round(card,18)) {
    g.FillPath(Brushes.White,path);
    using(var pen=new Pen(on?accents[index]:Color.FromArgb(214,224,242),on?1.6f:1f)) g.DrawPath(pen,path);
   }
   PaintGlyph(g,index,new Rectangle(card.X+22,card.Y+18,36,36));
   TextRenderer.DrawText(g,titles[index],cardFont,new Rectangle(card.X+68,card.Y+22,card.Width-90,36),ink,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);
   TextRenderer.DrawText(g,bodies[index],Font,new Rectangle(card.X+22,card.Y+70,card.Width-44,58),muted,TextFormatFlags.Left|TextFormatFlags.WordBreak);
   TextRenderer.DrawText(g,"开始",strongFont,new Rectangle(card.X+22,card.Bottom-36,card.Width-44,24),accents[index],TextFormatFlags.Left|TextFormatFlags.VerticalCenter);
  }
  static void PaintBroom(Graphics g,Rectangle box,Color color,Color gap) {
   float cx=box.X+box.Width*0.46f;
   float handleW=Math.Max(2f,box.Width*0.16f);
   float top=box.Y+box.Height*0.02f;
   float neck=box.Y+box.Height*0.50f;
   using(var brush=new SolidBrush(color)) {
    g.FillEllipse(brush,cx-handleW/2f,top,handleW,handleW);
    g.FillRectangle(brush,cx-handleW/2f,top+handleW*0.45f,handleW,neck-(top+handleW*0.45f));
    g.FillPolygon(brush,new[]{
     new PointF(cx-box.Width*0.22f,neck),
     new PointF(cx+box.Width*0.24f,neck),
     new PointF(cx+box.Width*0.46f,box.Y+box.Height*0.96f),
     new PointF(cx-box.Width*0.42f,box.Y+box.Height*0.96f)
    });
   }
   using(var pen=new Pen(gap,Math.Max(1.1f,box.Width*0.07f))) {
    pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;
    float y0=neck+box.Height*0.12f,y1=box.Y+box.Height*0.94f;
    g.DrawLine(pen,cx-box.Width*0.08f,y0,cx-box.Width*0.16f,y1);
    g.DrawLine(pen,cx+box.Width*0.02f,y0,cx,y1);
    g.DrawLine(pen,cx+box.Width*0.12f,y0,cx+box.Width*0.18f,y1);
   }
  }
  void PaintGlyph(Graphics g,int index,Rectangle box) {
   Color accent=accents[index];
   Color pale=Color.FromArgb(239,246,255);
   using(var tile=Round(box,10))
   using(var wash=new SolidBrush(pale)) g.FillPath(wash,tile);
   if(index==0) { PaintBroom(g,new Rectangle(box.X+7,box.Y+4,20,28),accent,pale); return; }
   if(index==1) {
    using(var pen=new Pen(accent,1.7f)) {
     pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;
     g.DrawLine(pen,box.X+10,box.Y+13,box.X+26,box.Y+13);
     g.DrawArc(pen,box.X+14,box.Y+7,8,8,200,140);
     using(var body=Round(new Rectangle(box.X+9,box.Y+15,18,13),3)) g.DrawPath(pen,body);
     g.DrawLine(pen,box.X+15,box.Y+18,box.X+15,box.Y+24);
     g.DrawLine(pen,box.X+21,box.Y+18,box.X+21,box.Y+24);
    }
    return;
   }
   float cx=box.X+18f,cy=box.Y+18f;
   var star=new PointF[8];
   for(int i=0;i<8;i++) {
    double angle=-Math.PI/2+i*Math.PI/4;
    float radius=i%2==0?9f:3.4f;
    star[i]=new PointF(cx+(float)Math.Cos(angle)*radius,cy+(float)Math.Sin(angle)*radius);
   }
   using(var fill=new SolidBrush(accent)) g.FillPolygon(fill,star);
  }
  protected override void OnMouseMove(MouseEventArgs e) {
   Measure();
   int next=-1;
   for(int i=0;i<cards.Length;i++) if(cards[i].Contains(e.Location)) next=i;
   if(next!=hover){hover=next;Cursor=next>=0?Cursors.Hand:Cursors.Default;Invalidate();}
   base.OnMouseMove(e);
  }
  protected override void OnMouseLeave(EventArgs e) {hover=-1;Cursor=Cursors.Default;Invalidate();base.OnMouseLeave(e);}
  protected override void OnMouseClick(MouseEventArgs e) {
   Measure();
   if(e.Button!=MouseButtons.Left){base.OnMouseClick(e);return;}
   EventHandler chosen=null;
   if(cards[0].Contains(e.Location)) chosen=DriveChosen;
   else if(cards[1].Contains(e.Location)) chosen=JunkChosen;
   else if(cards[2].Contains(e.Location)) chosen=AiChosen;
   if(chosen!=null) chosen(this,EventArgs.Empty);
   base.OnMouseClick(e);
  }
  internal static void DrawMark(Graphics g,Rectangle box){Portrait.Draw(g,box);}
  static GraphicsPath Round(Rectangle bounds,int radius) {
   int d=Math.Max(1,radius)*2;
   var path=new GraphicsPath();
   path.AddArc(bounds.X,bounds.Y,d,d,180,90);
   path.AddArc(bounds.Right-d,bounds.Y,d,d,270,90);
   path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90);
   path.AddArc(bounds.X,bounds.Bottom-d,d,d,90,90);
   path.CloseFigure();
   return path;
  }
 }
 internal static class Portrait {
  internal static void Draw(Graphics g,Rectangle box){
   int radius=Math.Max(8,box.Width/5),d=radius*2;
   using(var path=new GraphicsPath()){
    path.AddArc(box.X,box.Y,d,d,180,90);path.AddArc(box.Right-d,box.Y,d,d,270,90);path.AddArc(box.Right-d,box.Bottom-d,d,d,0,90);path.AddArc(box.X,box.Bottom-d,d,d,90,90);path.CloseFigure();
    var state=g.Save();g.SetClip(path);
    using(var wash=new SolidBrush(FishPainter.Well)) g.FillRectangle(wash,box);
    var face=PetFrames.Still;if(face!=null) g.DrawImage(face,box);else FishPainter.Draw(g,box,0,false,false);
    g.Restore(state);
    using(var pen=new Pen(Color.FromArgb(214,224,242))) g.DrawPath(pen,path);
   }
  }
 }
}
