using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Chargle.IconForge;

/// <summary>
/// Draws the Chargle mark and writes every size Windows asks for.
///
///     dotnet run --project tools/IconForge
///
/// The icon is a bolt with two arcs coming off it: charge, and the sound it makes. Identical at
/// every size, so the app is recognisably the same thing in the tray, the task bar and the Store.
/// Each size is drawn at its own resolution rather than scaled from one master, which is what
/// keeps the small ones sharp.
/// </summary>
public static class Program
{
    // One flat blue. Energetic without being a traffic light, and distinct from the battery
    // glyphs already living in the Windows tray.
    private static readonly Color Brand = Color.FromArgb(0xFF, 0x25, 0x63, 0xEB);

    public static int Main(string[] args)
    {
        string root = args.Length > 0 ? Path.GetFullPath(args[0]) : FindAssetsDirectory();
        Directory.CreateDirectory(root);

        // The .ico drives the exe, the window and the tray.
        int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
        WriteIco(Path.Combine(root, "Chargle.ico"), icoSizes);
        Console.WriteLine($"  Chargle.ico            {string.Join(", ", icoSizes)}");

        // A monochrome variant for the tray: Windows 11 tints tray glyphs to match the theme,
        // and a full-colour tile at 16 px next to the system icons looks like a sticker.
        WritePng(Path.Combine(root, "TrayLight.png"), 32, Render(32, TrayStyle.ForLightTaskbar));
        WritePng(Path.Combine(root, "TrayDark.png"), 32, Render(32, TrayStyle.ForDarkTaskbar));
        Console.WriteLine("  TrayLight.png / TrayDark.png");

        // MSIX visual assets. Names are fixed by the manifest schema.
        foreach ((string name, int w, int h) in StoreAssets())
        {
            WritePng(Path.Combine(root, name), w, h, RenderTile(w, h));
            Console.WriteLine($"  {name,-34} {w}x{h}");
        }

        Console.WriteLine();
        Console.WriteLine($"Written to {root}");
        return 0;
    }

    private static IEnumerable<(string Name, int Width, int Height)> StoreAssets()
    {
        yield return ("Square44x44Logo.png", 44, 44);
        yield return ("Square44x44Logo.targetsize-16.png", 16, 16);
        yield return ("Square44x44Logo.targetsize-24.png", 24, 24);
        yield return ("Square44x44Logo.targetsize-32.png", 32, 32);
        yield return ("Square44x44Logo.targetsize-48.png", 48, 48);
        yield return ("Square44x44Logo.targetsize-256.png", 256, 256);
        yield return ("Square71x71Logo.png", 71, 71);
        yield return ("Square150x150Logo.png", 150, 150);
        yield return ("Square310x310Logo.png", 310, 310);
        yield return ("Wide310x150Logo.png", 310, 150);
        yield return ("StoreLogo.png", 50, 50);
        yield return ("SplashScreen.png", 620, 300);
        yield return ("LockScreenLogo.png", 24, 24);
    }

    private enum TrayStyle { Full, ForLightTaskbar, ForDarkTaskbar }

    /// <summary>The square app tile: rounded background, gradient, bolt, arcs above 32 px.</summary>
    private static Bitmap Render(int size, TrayStyle style = TrayStyle.Full)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        Configure(g);

        var bounds = new RectangleF(0, 0, size, size);

        if (style == TrayStyle.Full)
        {
            float radius = size * 0.225f;
            using var background = RoundedRect(bounds, radius);
            using var brush = new SolidBrush(Brand);
            g.FillPath(brush, background);
        }

        Color ink = style switch
        {
            TrayStyle.ForLightTaskbar => Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),
            TrayStyle.ForDarkTaskbar => Color.White,
            _ => Color.White,
        };

        // The glyph sits in the middle 62% of the tile, which is the optical inset Windows 11
        // tiles use. Tray glyphs get the full square instead.
        float inset = style == TrayStyle.Full ? size * 0.19f : size * 0.06f;
        var glyph = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);

        DrawMark(g, glyph, ink);
        return bmp;
    }

    /// <summary>Wide and splash tiles: same mark, centred, on the same gradient.</summary>
    private static Bitmap RenderTile(int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        Configure(g);

        bool square = width == height;
        float radius = square ? Math.Min(width, height) * 0.225f : 0;

        using (var background = RoundedRect(new RectangleF(0, 0, width, height), radius))
        using (var brush = new SolidBrush(Brand))
        {
            g.FillPath(brush, background);
        }

        float glyphSize = Math.Min(width, height) * (square ? 0.62f : 0.52f);
        var glyph = new RectangleF(
            (width - glyphSize) / 2f, (height - glyphSize) / 2f, glyphSize, glyphSize);

        DrawMark(g, glyph, Color.White);
        return bmp;
    }

    /// <summary>
    /// The mark itself. A bolt, and two arcs radiating from its upper right like the emission
    /// lines on a speaker glyph.
    /// </summary>
    private static void DrawMark(Graphics g, RectangleF box, Color ink)
    {
        // The bolt lives entirely in the left 58% of the box. Its widest point is the lower
        // right tip at x = 0.58, which is the number the arcs below are positioned against.
        PointF[] bolt =
        [
            P(box, 0.411f, 0.080f),
            P(box, 0.040f, 0.553f),
            P(box, 0.259f, 0.553f),
            P(box, 0.209f, 0.920f),
            P(box, 0.580f, 0.430f),
            P(box, 0.361f, 0.430f),
        ];

        using (var path = new GraphicsPath())
        {
            path.AddPolygon(bolt);
            using var brush = new SolidBrush(ink);
            g.FillPath(brush, path);
        }

        // Two arcs, the outer one fainter, suggesting the sound leaving the bolt.
        //
        // The centre sits far to the left of where the arcs are actually drawn, so a narrow
        // sweep of a large circle lands as a shallow curve well clear of the bolt. The inner
        // radius is chosen so that at y = 0.43, where the bolt is at its widest, the arc passes
        // through x = 0.70: about an eighth of the box of clear space at the tightest point.
        float stroke = box.Width * 0.075f;
        for (int i = 0; i < 2; i++)
        {
            float radius = box.Width * (0.505f + i * 0.160f);
            var centre = P(box, 0.20f, 0.50f);
            var arc = new RectangleF(centre.X - radius, centre.Y - radius, radius * 2, radius * 2);

            using var pen = new Pen(Color.FromArgb(i == 0 ? 235 : 150, ink), stroke)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            g.DrawArc(pen, arc, -38, 76);
        }
    }

    private static PointF P(RectangleF box, float x, float y) =>
        new(box.X + box.Width * x, box.Y + box.Height * y);

    private static void Configure(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();

        if (radius <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }

        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void WritePng(string path, int size, Bitmap bmp) => WritePng(path, size, size, bmp);

    private static void WritePng(string path, int width, int height, Bitmap bmp)
    {
        using (bmp) bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>
    /// Writes a Vista-era .ico: a directory followed by PNG-compressed images. Every size is
    /// drawn at its own resolution rather than scaled down from one big one, so the 16 px entry
    /// gets its own simplified artwork instead of a blurry reduction.
    /// </summary>
    private static void WriteIco(string path, int[] sizes)
    {
        var images = new List<byte[]>();

        foreach (int size in sizes)
        {
            using var stream = new MemoryStream();
            using (var bmp = Render(size)) bmp.Save(stream, ImageFormat.Png);
            images.Add(stream.ToArray());
        }

        using var file = File.Create(path);
        using var w = new BinaryWriter(file);

        w.Write((ushort)0);                // reserved
        w.Write((ushort)1);                // type: icon
        w.Write((ushort)sizes.Length);

        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            // 256 is encoded as 0 in the directory, which is the quirk that makes .ico files
            // with a 256 px entry fail when written naively.
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);              // palette size
            w.Write((byte)0);              // reserved
            w.Write((ushort)1);            // colour planes
            w.Write((ushort)32);           // bits per pixel
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }

        foreach (byte[] image in images) w.Write(image);
    }

    private static string FindAssetsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Chargle.slnx")))
                return Path.Combine(dir.FullName, "src", "Chargle", "Assets");
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the Chargle solution. Pass an output directory explicitly.");
    }
}
