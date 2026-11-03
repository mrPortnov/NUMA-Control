using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NumaControl
{
    internal sealed class TrayStyle : ToolStripProfessionalRenderer
    {
        private static Color BackgroundColor { get { return ColorTranslator.FromHtml(Themes.Color("Panel")); } }
        private static Color Accent { get { return ColorTranslator.FromHtml(Themes.Color("Accent")); } }
        private sealed class Assets : IDisposable
        {
            internal Font Font;
            internal readonly System.Collections.Generic.List<Image> Images = new System.Collections.Generic.List<Image>();
            public void Dispose() { foreach (Image image in Images) image.Dispose(); Images.Clear(); if (Font != null) Font.Dispose(); }
        }
        internal static void Apply(ContextMenuStrip menu)
        {
            menu.Renderer = new TrayStyle(); menu.ShowCheckMargin = false; menu.ShowImageMargin = true;
            menu.Padding = new Padding(7); menu.ImageScalingSize = new Size(20, 20);
            foreach (ToolStripItem item in menu.Items)
            {
                if (item is ToolStripSeparator) { item.Margin = new Padding(5, 4, 5, 4); continue; }
                item.Padding = new Padding(5, 7, 12, 7);
            }
            menu.Opened += delegate
            {
                using (GraphicsPath path = Rounded(new Rectangle(0, 0, menu.Width, menu.Height), 11))
                { Region old = menu.Region; menu.Region = new Region(path); if (old != null) old.Dispose(); }
            };
            menu.Disposed += delegate { Assets assets = menu.Tag as Assets; if (assets != null) assets.Dispose(); };
            Refresh(menu);
        }
        internal static void Refresh(ContextMenuStrip menu)
        {
            Assets old = menu.Tag as Assets; Assets assets = new Assets();
            menu.BackColor = BackgroundColor; menu.ForeColor = ColorTranslator.FromHtml(Themes.Color("Text"));
            assets.Font = new Font(Themes.Current == "ami-bios" ? "Consolas" : "Segoe UI", Themes.Current == "ami-bios" ? 11 : 10); menu.Font = assets.Font;
            foreach (ToolStripItem item in menu.Items)
            {
                if (item is ToolStripSeparator) continue;
                item.ForeColor = ColorTranslator.FromHtml(Themes.Color(item.Text == L10n.T("Выход") ? "Error" : "Text")); item.Image = Glyph(item.Text); assets.Images.Add(item.Image);
            }
            menu.Tag = assets; if (old != null) old.Dispose();
        }
        private static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            if (Themes.Current == "ami-bios") { GraphicsPath square = new GraphicsPath(); square.AddRectangle(rect); return square; }
            GraphicsPath path = new GraphicsPath(); int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90); path.AddArc(rect.Right-d-1, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right-d-1, rect.Bottom-d-1, d, d, 0, 90); path.AddArc(rect.X, rect.Bottom-d-1, d, d, 90, 90); path.CloseFigure(); return path;
        }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        { e.Graphics.Clear(BackgroundColor); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (GraphicsPath path = Rounded(new Rectangle(0,0,e.ToolStrip.Width,e.ToolStrip.Height),11)) using (Pen pen = new Pen(ColorTranslator.FromHtml(Themes.Color("Line")))) e.Graphics.DrawPath(pen,path); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Rounded(new Rectangle(2,1,e.Item.Width-4,e.Item.Height-2),6)) using (Brush brush = new SolidBrush(ColorTranslator.FromHtml(Themes.Color("Hover")))) e.Graphics.FillPath(brush,path);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        { using (Pen pen = new Pen(ColorTranslator.FromHtml(Themes.Color("Line")))) e.Graphics.DrawLine(pen,7,e.Item.Height/2,e.Item.Width-7,e.Item.Height/2); }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? ColorTranslator.FromHtml(Themes.Color("Muted")) : e.Item.ForeColor;
            e.TextRectangle = new Rectangle(e.TextRectangle.Left, 0, e.Item.Width - e.TextRectangle.Left - 12, e.Item.Height);
            e.TextFormat = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; Rectangle r=e.ImageRectangle;
            using (Brush brush = new SolidBrush(BackgroundColor)) e.Graphics.FillRectangle(brush,r);
            using (Pen pen = new Pen(Accent,2)) e.Graphics.DrawLines(pen,new[]{new Point(r.Left+3,r.Top+8),new Point(r.Left+7,r.Top+12),new Point(r.Left+14,r.Top+4)});
        }
        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            base.OnRenderItemImage(e);
        }
        private static Bitmap Glyph(string text)
        {
            Bitmap image = new Bitmap(96,96); using(Graphics g=Graphics.FromImage(image))
            {
                g.ScaleTransform(4,4); g.SmoothingMode=SmoothingMode.AntiAlias; g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                Color color = text==L10n.T("Выход")?ColorTranslator.FromHtml(Themes.Color("Error")):Accent;
                using(Pen p=new Pen(color,1.7f))
                {
                    p.StartCap=p.EndCap=LineCap.Round; p.LineJoin=LineJoin.Round;
                    if(text.StartsWith(L10n.T("Открыть"),StringComparison.Ordinal)) { using(GraphicsPath path=Rounded(new Rectangle(3,4,19,17),3)) g.DrawPath(p,path); g.DrawLine(p,4,9,20,9); }
                    else if(text.StartsWith(L10n.T("Пауза"),StringComparison.Ordinal)) { using(Brush b=new SolidBrush(color)) { g.FillRectangle(b,6,5,4,14); g.FillRectangle(b,14,5,4,14); } }
                    else if(text.StartsWith(L10n.T("Автозапуск"),StringComparison.Ordinal)) { if (Themes.Current == "ami-bios") { g.DrawLines(p,new[]{new Point(18,4),new Point(4,4),new Point(4,20),new Point(20,20),new Point(20,10)}); g.DrawLines(p,new[]{new Point(14,1),new Point(18,4),new Point(14,7)}); } else { g.DrawArc(p,4,5,16,16,-45,270); g.DrawLine(p,12,2,12,12); } }
                    else if(text==L10n.T("Выход")) { g.DrawLines(p,new[]{new Point(12,4),new Point(4,4),new Point(4,20),new Point(12,20)}); g.DrawLine(p,10,12,21,12); g.DrawLines(p,new[]{new Point(17,8),new Point(21,12),new Point(17,16)}); }
                    else g.DrawLines(p,new[]{new Point(2,12),new Point(7,12),new Point(10,4),new Point(14,20),new Point(17,11),new Point(22,11)});
                }
            } return image;
        }
    }
}
