// Draws the VibeChat icon at each size and writes a multi-size .ico (PNG frames) plus a preview sheet.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class IconGen
{
    static readonly Color Top = Color.FromArgb(0xFF, 0x5F, 0x6D);    // coral
    static readonly Color Bottom = Color.FromArgb(0xFF, 0xB3, 0x47); // amber

    static Bitmap Render(int s)
    {
        Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float m = Math.Max(0.5f, s * 0.03f);
            RectangleF body = new RectangleF(m, m + s * 0.02f, s - 2 * m, s * 0.76f);
            float r = body.Height * 0.30f;
            using (GraphicsPath p = new GraphicsPath(FillMode.Winding))
            {
                p.AddArc(body.X, body.Y, 2 * r, 2 * r, 180, 90);
                p.AddArc(body.Right - 2 * r, body.Y, 2 * r, 2 * r, 270, 90);
                p.AddArc(body.Right - 2 * r, body.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
                p.AddArc(body.X, body.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
                p.CloseFigure();
                // tail: a short, wide wedge from the flat part of the bottom edge, leaning right
                p.AddPolygon(new PointF[] {
                    new PointF(body.Right - s * 0.46f, body.Bottom - s * 0.08f),
                    new PointF(body.Right - s * 0.20f, body.Bottom - s * 0.08f),
                    new PointF(body.Right - s * 0.13f, s - m)
                });
                // gradient spans the whole square so nothing falls past its end (GDI+ would restart the colours)
                using (LinearGradientBrush br = new LinearGradientBrush(new PointF(-1, -1), new PointF(s + 1, s + 1), Top, Bottom))
                    g.FillPath(br, p);
            }

            // equalizer bars ("the vibe"), snapped to whole pixels at small sizes
            float[] rel = { 0.50f, 1.00f, 0.70f };
            float w = body.Width * 0.14f, gap = w * 0.75f, maxH = body.Height * 0.60f;
            bool small = s <= 32;
            if (small) { w = Math.Max(2, (float)Math.Round(w)); gap = Math.Max(1, (float)Math.Round(gap)); maxH = (float)Math.Round(maxH); }
            float total = rel.Length * w + (rel.Length - 1) * gap;
            float x = body.X + (body.Width - total) / 2f, cy = body.Y + body.Height / 2f;
            if (small) { x = (float)Math.Round(x); cy = (float)Math.Round(cy); }
            g.PixelOffsetMode = small ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            foreach (float k in rel)
            {
                float h = maxH * k;
                if (small) h = Math.Max(2, (float)Math.Round(h / 2) * 2);
                RectangleF bar = new RectangleF(x, cy - h / 2f, w, h);
                if (s <= 24) g.FillRectangle(Brushes.White, bar);
                else
                    using (GraphicsPath bp = Rounded(bar, w / 2f)) g.FillPath(Brushes.White, bp);
                x += w + gap;
            }
        }
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF b, float r)
    {
        GraphicsPath p = new GraphicsPath();
        p.AddArc(b.X, b.Y, 2 * r, 2 * r, 180, 180);
        p.AddArc(b.X, b.Bottom - 2 * r, 2 * r, 2 * r, 0, 180);
        p.CloseFigure();
        return p;
    }

    static void Main(string[] args)
    {
        string icoPath = args[0], previewPath = args[1];
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        List<byte[]> pngs = new List<byte[]>();
        List<Bitmap> frames = new List<Bitmap>();
        foreach (int s in sizes)
        {
            Bitmap b = Render(s);
            frames.Add(b);
            using (MemoryStream ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
        }
        using (BinaryWriter w = new BinaryWriter(File.Create(icoPath)))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (byte[] p in pngs) w.Write(p);
        }

        // Preview: every size at 1:1 and the small ones blown up 4x, on light and dark, plus the app's unread badge at 32px.
        using (Bitmap sheet = new Bitmap(900, 560))
        using (Graphics g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.White);
            g.FillRectangle(new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20)), 0, 280, 900, 280);
            for (int band = 0; band < 2; band++)
            {
                int y0 = band * 280 + 12, x = 12;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                for (int i = 0; i < sizes.Length - 1; i++) { g.DrawImage(frames[i], x, y0); x += sizes[i] + 10; }
                g.DrawImage(frames[sizes.Length - 1], 640, y0, 256, 256);
                x = 12;
                for (int i = 0; i < 4; i++) { int s = sizes[i]; g.DrawImage(frames[i], new Rectangle(x, y0 + 140, s * 4, s * 4)); x += s * 4 + 12; }
                // unread badge as drawn by the app (BadgedIcon)
                using (Bitmap badge = new Bitmap(32, 32))
                using (Graphics bg = Graphics.FromImage(badge))
                {
                    bg.SmoothingMode = SmoothingMode.AntiAlias;
                    bg.DrawImage(frames[3], 0, 0, 32, 32);
                    bg.FillEllipse(Brushes.White, 17, 17, 15, 15);
                    using (SolidBrush red = new SolidBrush(Color.FromArgb(0xe0, 0x2e, 0x2e))) bg.FillEllipse(red, 19, 19, 11, 11);
                    g.DrawImage(badge, new Rectangle(500, y0 + 140, 128, 128));
                }
            }
            sheet.Save(previewPath, ImageFormat.Png);
        }
        Console.WriteLine("wrote " + icoPath);
    }
}
