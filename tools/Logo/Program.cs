using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Draws the plugin logo: a bright star with three linked telescopes on an
// orbit around it, on a night-blue tile.
//   dotnet run --project tools/Logo -- assets/starfront-targetscheduler-collab.png [size]
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var output = args.Length > 0 ? args[0] : "logo.png";
        var size = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 512;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 512.0, size / 512.0));
            Draw(dc);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var file = File.Create(output);
        encoder.Save(file);
    }

    private static void Draw(DrawingContext dc)
    {
        var tile = new LinearGradientBrush(Color.FromRgb(0x0A, 0x14, 0x2E), Color.FromRgb(0x17, 0x2F, 0x5C), new Point(0, 0), new Point(1, 1));
        dc.DrawRoundedRectangle(tile, null, new Rect(8, 8, 496, 496), 96, 96);

        // A few faint background stars.
        var faint = new SolidColorBrush(Color.FromArgb(0x90, 0xC9, 0xD8, 0xF2));
        foreach (var (x, y, r) in new[] { (92.0, 110.0, 3.0), (400.0, 92.0, 2.5), (430.0, 300.0, 2.0), (120.0, 410.0, 2.5), (330.0, 440.0, 2.0), (70.0, 260.0, 2.0), (250.0, 70.0, 2.0) })
            dc.DrawEllipse(faint, null, new Point(x, y), r, r);

        var center = new Point(256, 256);
        // The orbit the collaborating telescopes share.
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x8F, 0xB8, 0xF0)), 6), center, 170, 170);

        var nodes = new[] { At(center, 170, -90), At(center, 170, 30), At(center, 170, 150) };
        var link = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x9F, 0xE7, 0xDF)), 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (var i = 0; i < nodes.Length; i++)
        {
            dc.DrawLine(link, nodes[i], nodes[(i + 1) % nodes.Length]);
        }

        // The star at the centre, with a soft glow.
        var glow = new RadialGradientBrush(Color.FromArgb(0xC0, 0xFF, 0xE8, 0xB0), Color.FromArgb(0x00, 0xFF, 0xE8, 0xB0));
        dc.DrawEllipse(glow, null, center, 120, 120);
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xDE)), null, Sparkle(center, 104, 20));

        var colours = new[] { Color.FromRgb(0x4F, 0xD1, 0xC5), Color.FromRgb(0x63, 0xB3, 0xED), Color.FromRgb(0xF6, 0xAD, 0x55) };
        for (var i = 0; i < nodes.Length; i++)
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0A, 0x14, 0x2E)), null, nodes[i], 40, 40);
            dc.DrawEllipse(new SolidColorBrush(colours[i]), null, nodes[i], 30, 30);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), null, nodes[i], 10, 10);
        }
    }

    private static Point At(Point center, double radius, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
    }

    /// A four-pointed star: long points on the axes, a narrow waist between them.
    private static Geometry Sparkle(Point c, double outer, double inner)
    {
        var figure = new PathFigure { StartPoint = new Point(c.X, c.Y - outer), IsClosed = true, IsFilled = true };
        for (var i = 1; i < 8; i++)
        {
            var radius = i % 2 == 0 ? outer : inner;
            figure.Segments.Add(new LineSegment(At(c, radius, -90 + i * 45), true));
        }
        return new PathGeometry([figure]);
    }
}
