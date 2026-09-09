using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LogoGenerator;

/// <summary>
/// Генератор иконки/логотипа DiskCleaner.
/// Концепция: тёмно-синяя плитка с неоновым акцентом, жёсткий диск + метла, сметающая пыль.
/// Вектор рисуется через WPF DrawingContext, растрируется в PNG и упаковывается в ICO (PNG-entries).
/// </summary>
public static class Program
{
    private static readonly Color Neon = Color.FromRgb(0x33, 0xE6, 0xFF);
    private static readonly Color NeonSoft = Color.FromRgb(0x66, 0xEF, 0xFF);
    private static readonly Color MetalTop = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color MetalBottom = Color.FromRgb(0xBF, 0xD3, 0xEC);
    private static readonly Color Hub = Color.FromRgb(0x21, 0x38, 0x62);
    private static readonly Color HubEdge = Color.FromRgb(0x46, 0x6A, 0xA8);
    private static readonly Color HandleTop = Color.FromRgb(0xEC, 0xF4, 0xFF);
    private static readonly Color HandleBottom = Color.FromRgb(0xA8, 0xC1, 0xE2);
    private static readonly Color Ferrule = Color.FromRgb(0x8F, 0xA9, 0xCF);
    private static readonly Color FerruleDark = Color.FromRgb(0x39, 0x4F, 0x77);
    private static readonly Color BristleDeep = Color.FromRgb(0x0B, 0x9F, 0xCE);

    [STAThread]
    public static int Main(string[] args)
    {
        string outDir = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "out");

        Directory.CreateDirectory(outDir);

        SavePng(Render(256, 2), Path.Combine(outDir, "DiskCleaner_256.png"));
        SavePng(Render(512, 1), Path.Combine(outDir, "DiskCleaner_512.png"));

        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        byte[] ico = BuildIco(sizes.Select(s => (s, Render(s, 4))).ToArray());
        File.WriteAllBytes(Path.Combine(outDir, "DiskCleaner.ico"), ico);

        Console.WriteLine($"OK -> {outDir}");
        return 0;
    }

    private static byte[] Render(int size, int supersample)
    {
        int px = size * supersample;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            Draw(dc, px);
        }

        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        if (supersample == 1)
            return EncodePng(rtb);

        var scaled = new TransformedBitmap(rtb, new ScaleTransform(1.0 / supersample, 1.0 / supersample));
        scaled.Freeze();
        return EncodePng(scaled);
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void SavePng(byte[] png, string path)
    {
        File.WriteAllBytes(path, png);
    }

    private static byte[] BuildIco((int Size, byte[] Png)[] frames)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)frames.Length);

        uint offset = (uint)(6 + 16 * frames.Length);
        foreach (var (size, png) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write((uint)png.Length);
            w.Write(offset);
            offset += (uint)png.Length;
        }

        foreach (var (_, png) in frames)
            w.Write(png);

        w.Flush();
        return ms.ToArray();
    }

    // ------------------------------------------------------------------
    // Рисование
    // ------------------------------------------------------------------

    private static void Draw(DrawingContext dc, double s)
    {
        double u = s / 256.0; // масштаб из базовой сетки 256
        dc.PushTransform(new ScaleTransform(u, u));

        var full = new Rect(0, 0, 256, 256);
        dc.DrawRectangle(Brushes.Transparent, null, full);

        const double m = 18, r = 58;
        var bgRect = new Rect(m, m, 256 - 2 * m, 256 - 2 * m);
        var bg = new LinearGradientBrush(Color.FromRgb(0x24, 0x44, 0x7E), Color.FromRgb(0x07, 0x0D, 0x1E), 30);
        var bgPath = RoundedRect(bgRect, r, r);
        dc.DrawGeometry(bg, null, bgPath);

        var rimPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 0x9B, 0xF0, 0xFF)), 1.5);
        dc.DrawGeometry(null, rimPen, bgPath);

        // неоновое свечение за диском
        var glow = new RadialGradientBrush
        {
            Center = new Point(0.47, 0.55),
            GradientOrigin = new Point(0.47, 0.55),
            RadiusX = 0.6,
            RadiusY = 0.6,
        };
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(38, Neon.R, Neon.G, Neon.B), 0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, Neon.R, Neon.G, Neon.B), 1));
        dc.DrawEllipse(glow, null, new Point(120, 140), 120, 120);

        DrawDisk(dc);
        DrawBroom(dc);
        DrawDust(dc);

        dc.Pop();
    }

    private static void DrawDisk(DrawingContext dc)
    {
        var center = new Point(116, 144);
        const double rOuter = 86;
        const double rHole = 43;

        var metal = new LinearGradientBrush(MetalTop, MetalBottom, 90);
        dc.DrawGeometry(metal, null, Ring(center, rOuter, rHole));

        // мягкая тень снизу кольца
        var shadow = new LinearGradientBrush(Color.FromArgb(0, 0x07, 0x0D, 0x1E), Color.FromArgb(110, 0x07, 0x0D, 0x1E), 90);
        dc.PushTransform(new TranslateTransform(0, 3.5));
        dc.DrawGeometry(shadow, null, Ring(center, rOuter, rHole));
        dc.Pop();

        // ступица в отверстии
        var hubBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.38, 0.36),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.95,
            RadiusY = 0.95,
        };
        hubBrush.GradientStops.Add(new GradientStop(HubEdge, 0));
        hubBrush.GradientStops.Add(new GradientStop(Hub, 1));
        dc.DrawEllipse(hubBrush, null, center, rHole, rHole);

        // блик на ступице
        var hubGloss = new LinearGradientBrush(
            Color.FromArgb(60, 0xFF, 0xFF, 0xFF), Colors.Transparent, 90);
        dc.DrawEllipse(hubGloss, null, new Point(center.X, center.Y - 22), 26, 13);

        // центральный шпиндель
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0A, 0x12, 0x26)), null, center, 6, 6);
        var spindle = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.4, 0.38),
            Center = new Point(0.5, 0.5),
            RadiusX = 1,
            RadiusY = 1,
        };
        spindle.GradientStops.Add(new GradientStop(Color.FromRgb(0x52, 0x6F, 0xA6), 0));
        spindle.GradientStops.Add(new GradientStop(Color.FromRgb(0x16, 0x25, 0x45), 1));
        dc.DrawEllipse(spindle, null, center, 6, 6);

        // блик сверху кольца (серп)
        var gloss = new LinearGradientBrush(Color.FromArgb(150, 0xFF, 0xFF, 0xFF), Colors.Transparent, 90);
        dc.DrawGeometry(gloss, null, Annulus(center, rOuter - 7, rOuter + 1, 200, 340));
    }

    private static void DrawBroom(DrawingContext dc)
    {
        // Поворот +30°: ручка уходит вверх-вправо, щетина метёт вниз-влево.
        const double angle = 30;
        var ferrule = new Point(108, 184);

        dc.PushTransform(new TranslateTransform(ferrule.X, ferrule.Y));
        dc.PushTransform(new RotateTransform(angle));

        DrawBroomShadow(dc);
        DrawBroomBody(dc);

        dc.Pop();
        dc.Pop();
    }

    private static void DrawBroomShadow(DrawingContext dc)
    {
        dc.PushTransform(new TranslateTransform(5, 7));
        dc.PushOpacity(0.42);

        var black = new SolidColorBrush(Color.FromRgb(0x02, 0x06, 0x0E));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            // ручка с наконечником
            c.BeginFigure(new Point(-7.5, -168), true, true);
            c.ArcTo(new Point(7.5, -168), new Size(7.5, 7.5), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(7.5, -6), true, true);
            c.LineTo(new Point(-7.5, -6), true, true);

            // ферруль
            c.BeginFigure(new Point(-12, -42), true, true);
            c.LineTo(new Point(12, -42), true, true);
            c.ArcTo(new Point(12, -16), new Size(6, 6), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(-12, -16), true, true);
            c.ArcTo(new Point(-12, -42), new Size(6, 6), 0, false, SweepDirection.Clockwise, true, true);

            // щетина (веер)
            c.BeginFigure(new Point(-14, -8), true, true);
            c.LineTo(new Point(14, -8), true, true);
            c.LineTo(new Point(42, 58), true, true);
            c.LineTo(new Point(-56, 62), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(black, null, g);

        dc.Pop();
        dc.Pop();
    }

    private static void DrawBroomBody(DrawingContext dc)
    {
        // ручка
        var handleBrush = new LinearGradientBrush(HandleTop, HandleBottom, 90);
        dc.DrawGeometry(handleBrush, null, RoundedRect(new Rect(-7.5, -168, 15, 132), 7.5, 7.5));
        dc.DrawEllipse(handleBrush, null, new Point(0, -168), 7.5, 7.5);

        // ферруль
        var ferruleBrush = new LinearGradientBrush(Ferrule, FerruleDark, 90);
        dc.DrawGeometry(ferruleBrush, null, RoundedRect(new Rect(-12, -42, 24, 26), 6, 6));
        var seamPen = new Pen(new SolidColorBrush(FerruleDark), 2.2);
        dc.DrawLine(seamPen, new Point(-12, -29), new Point(12, -29));
        var lightPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 0xFF, 0xFF, 0xFF)), 1.6);
        dc.DrawLine(lightPen, new Point(-12, -20), new Point(12, -20));

        // щетина
        var bristleBrush = new LinearGradientBrush(NeonSoft, BristleDeep, 90);
        var fan = new StreamGeometry();
        using (var g = fan.Open())
        {
            g.BeginFigure(new Point(-14, -8), true, true);
            g.LineTo(new Point(14, -8), true, true);
            g.LineTo(new Point(42, 58), true, true);
            g.LineTo(new Point(-56, 62), true, true);
        }
        fan.Freeze();
        dc.DrawGeometry(bristleBrush, null, fan);

        // прожилки щетины
        var veinPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 0x04, 0x59, 0x7E)), 1.5);
        for (int i = 1; i <= 5; i++)
        {
            double t = i / 6.0;
            double x = -14 + (42 + 14) * t;
            double y = -8 + (58 + 8) * t;
            dc.DrawLine(veinPen, new Point(x - 8, y), new Point(x + 8, y));
        }
    }

    private static void DrawDust(DrawingContext dc)
    {
        var motes = new[]
        {
            new { P = new Point(66, 232), R = 6.5, C = NeonSoft },
            new { P = new Point(46, 220), R = 4.2, C = Neon },
            new { P = new Point(86, 226), R = 3.4, C = Colors.White },
            new { P = new Point(40, 202), R = 3.0, C = Neon },
            new { P = new Point(60, 249), R = 2.8, C = Colors.White },
            new { P = new Point(31, 233), R = 2.4, C = Neon },
        };
        foreach (var mote in motes)
        {
            dc.DrawEllipse(new SolidColorBrush(mote.C), null, mote.P, mote.R, mote.R);
        }

        // искра-звёздочка
        var spark = new StreamGeometry();
        using (var g = spark.Open())
        {
            var c = new Point(97, 234);
            const double big = 10, small = 3.4;
            g.BeginFigure(new Point(c.X, c.Y - big), true, true);
            g.LineTo(new Point(c.X + small, c.Y - small), true, true);
            g.LineTo(new Point(c.X + big, c.Y), true, true);
            g.LineTo(new Point(c.X + small, c.Y + small), true, true);
            g.LineTo(new Point(c.X, c.Y + big), true, true);
            g.LineTo(new Point(c.X - small, c.Y + small), true, true);
            g.LineTo(new Point(c.X - big, c.Y), true, true);
            g.LineTo(new Point(c.X - small, c.Y - small), true, true);
        }
        spark.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Neon), null, spark);
    }

    // ------------------------------------------------------------------
    // Вспомогательные геометрии
    // ------------------------------------------------------------------

    private static Point PointOn(Point c, double r, double deg)
    {
        double rad = deg * Math.PI / 180.0;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    private static Geometry Ring(Point center, double outer, double inner)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(PointOn(center, outer, 0), true, true);
            ctx.ArcTo(PointOn(center, outer, 180), new Size(outer, outer), 0, false, SweepDirection.Clockwise, true, true);
            ctx.ArcTo(PointOn(center, outer, 360), new Size(outer, outer), 0, false, SweepDirection.Clockwise, true, true);
            ctx.BeginFigure(PointOn(center, inner, 0), true, true);
            ctx.ArcTo(PointOn(center, inner, 180), new Size(inner, inner), 0, false, SweepDirection.Clockwise, true, true);
            ctx.ArcTo(PointOn(center, inner, 360), new Size(inner, inner), 0, false, SweepDirection.Clockwise, true, true);
        }
        g.FillRule = FillRule.EvenOdd;
        g.Freeze();
        return g;
    }

    /// <summary>Кольцевой сегмент между радиусами r0/r1 в диапазоне углов a0..a1 (градусы, CW).</summary>
    private static Geometry Annulus(Point center, double r0, double r1, double a0, double a1)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(PointOn(center, r0, a0), true, true);
            ctx.ArcTo(PointOn(center, r0, a1), new Size(r0, r0), 0, false, SweepDirection.Clockwise, true, true);
            ctx.ArcTo(PointOn(center, r1, a1), new Size(r1, r1), 0, false, SweepDirection.Clockwise, true, true);
            ctx.ArcTo(PointOn(center, r1, a0), new Size(r1, r1), 0, false, SweepDirection.Counterclockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    private static Geometry RoundedRect(Rect rect, double rX, double rY)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(rect.Left + rX, rect.Top), true, true);
            ctx.LineTo(new Point(rect.Right - rX, rect.Top), true, true);
            ctx.ArcTo(new Point(rect.Right, rect.Top + rY), new Size(rX, rY), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(rect.Right, rect.Bottom - rY), true, true);
            ctx.ArcTo(new Point(rect.Right - rX, rect.Bottom), new Size(rX, rY), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(rect.Left + rX, rect.Bottom), true, true);
            ctx.ArcTo(new Point(rect.Left, rect.Bottom - rY), new Size(rX, rY), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(rect.Left, rect.Top + rY), true, true);
            ctx.ArcTo(new Point(rect.Left + rX, rect.Top), new Size(rX, rY), 0, false, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }
}
