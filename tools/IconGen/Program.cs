using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Draws the VeloDM icon natively at each size (no downscaling blur) and writes a
// multi-resolution .ico plus the PNGs the browser extension needs.
var root = FindRepoRoot();
var icoPath = Path.Combine(root, "src", "VeloDM.App", "Assets", "VeloDM.ico");
Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
WriteIco(icoPath, [16, 20, 24, 32, 40, 48, 64, 128, 256]);
Console.WriteLine(icoPath);

var extDir = Path.Combine(root, "extension", "icons");
Directory.CreateDirectory(extDir);
foreach (var size in new[] { 16, 32, 48, 128 })
{
    using var bmp = Draw(size);
    var path = Path.Combine(extDir, $"icon{size}.png");
    bmp.Save(path, ImageFormat.Png);
    Console.WriteLine(path);
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VeloDM.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the VeloDM repository.");
}

static Bitmap Draw(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.ScaleTransform(size / 256f, size / 256f);

    // Small sizes get a bolder, edge-to-edge layout so the arrow stays readable at 16 px.
    bool small = size <= 24;
    float margin = small ? 4 : 12, radius = small ? 52 : 60;
    var tile = new RectangleF(margin, margin, 256 - 2 * margin, 256 - 2 * margin);

    using (var path = RoundedRect(tile, radius))
    using (var fill = new LinearGradientBrush(tile, Color.FromArgb(0x1E, 0xC8, 0xFF), Color.FromArgb(0x3A, 0x3F, 0xE0), 55f))
    {
        g.FillPath(fill, path);
        if (!small)
        {
            // Soft highlight across the top half.
            var top = new RectangleF(tile.X, tile.Y, tile.Width, tile.Height * 0.55f);
            using var clip = RoundedRect(tile, radius);
            g.SetClip(clip);
            using var gloss = new LinearGradientBrush(top, Color.FromArgb(56, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f);
            g.FillRectangle(gloss, top);
            g.ResetClip();
        }
    }

    // "V" for Velo: a bold chevron that also points down like a download arrow, leaning
    // forward for speed, with a tray bar under its point.
    float shift = small ? 0 : 14;
    PointF[] v = small
        ? [new(36, 28), new(96, 28), new(128, 116), new(160, 28), new(220, 28), new(156, 180), new(100, 180)]
        : [new(64, 52), new(108, 52), new(128, 120), new(148, 52), new(192, 52), new(150, 172), new(106, 172)];
    var bar = small ? new RectangleF(64, 200, 128, 34) : new RectangleF(84, 192, 88, 22);

    var state = g.Save();
    g.TranslateTransform(shift + 0.2f * 128, 0);
    g.MultiplyTransform(new Matrix(1, 0, -0.2f, 1, 0, 0)); // x' = x + 0.2 * (128 - y)
    using (var white = new SolidBrush(Color.White))
    using (var round = new Pen(Color.White, small ? 6 : 10) { LineJoin = LineJoin.Round })
    using (var vPath = new GraphicsPath())
    using (var barPath = RoundedRect(bar, small ? 8 : 7))
    {
        vPath.AddPolygon(v);
        g.FillPath(white, vPath);
        g.DrawPath(round, vPath);
        g.FillPath(white, barPath);
    }
    g.Restore(state);

    // Speed streaks trailing behind the V, only where there is room for them.
    if (!small)
    {
        using var streak = new Pen(Color.FromArgb(170, 255, 255, 255), 13) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(streak, 34, 84, 78, 84);
        g.DrawLine(streak, 48, 116, 84, 116);
        g.DrawLine(streak, 62, 148, 92, 148);
    }

    return bmp;
}

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    var path = new GraphicsPath();
    float d = radius * 2;
    path.AddArc(r.X, r.Y, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

// ICO: 32-bit BMP entries below 256 px (widest compatibility), PNG for 256 px.
static void WriteIco(string path, int[] sizes)
{
    var images = sizes.Select(s => s >= 256 ? Png(s) : Dib(s)).ToList();
    using var file = File.Create(path);
    using var w = new BinaryWriter(file);
    w.Write((short)0);
    w.Write((short)1);
    w.Write((short)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(images[i].Length);
        w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var image in images) w.Write(image);
}

static byte[] Png(int size)
{
    using var bmp = Draw(size);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static byte[] Dib(int size)
{
    using var bmp = Draw(size);
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    int maskStride = (size + 31) / 32 * 4;
    w.Write(40);
    w.Write(size);
    w.Write(size * 2); // XOR bitmap + AND mask
    w.Write((short)1);
    w.Write((short)32);
    w.Write(0);
    w.Write(size * size * 4 + maskStride * size);
    w.Write(0);
    w.Write(0);
    w.Write(0);
    w.Write(0);
    for (int y = size - 1; y >= 0; y--)
        for (int x = 0; x < size; x++)
        {
            var c = bmp.GetPixel(x, y);
            w.Write(c.B);
            w.Write(c.G);
            w.Write(c.R);
            w.Write(c.A);
        }
    for (int y = size - 1; y >= 0; y--)
    {
        var row = new byte[maskStride];
        for (int x = 0; x < size; x++)
            if (bmp.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
        w.Write(row);
    }
    return ms.ToArray();
}
