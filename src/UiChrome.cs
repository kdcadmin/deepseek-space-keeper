using System;
using System.Drawing;
using System.Windows.Forms;

namespace ClearDisk {
 internal sealed class DriveUsageBar : Control {
  double ratio;
  internal double Ratio {
   get { return ratio; }
   set { double next = value < 0 ? 0 : (value > 1 ? 1 : value); if (next == ratio) return; ratio = next; Invalidate(); }
  }
  internal DriveUsageBar() {
   SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
   Height = 14; Dock = DockStyle.Fill; Margin = new Padding(0, 2, 0, 0);
  }
  protected override void OnPaint(PaintEventArgs e) {
   Rectangle track = new Rectangle(0, 2, Math.Max(1, Width - 1), Math.Max(1, Height - 5));
   using (SolidBrush trackBrush = new SolidBrush(Color.FromArgb(226, 234, 246))) e.Graphics.FillRectangle(trackBrush, track);
   int fill = (int)Math.Round(track.Width * ratio);
   if (fill <= 0) return;
   Color used = ratio >= 0.9 ? Color.FromArgb(226, 62, 74) : Color.FromArgb(31, 79, 184);
   using (SolidBrush fillBrush = new SolidBrush(used)) e.Graphics.FillRectangle(fillBrush, track.X, track.Y, fill, track.Height);
  }
 }
}
