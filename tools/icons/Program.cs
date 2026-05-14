using SkiaSharp;
using Svg.Skia;

// ============================================================================
// IconGen — rasterise the Cinder ember SVG into every asset shape the app needs.
// ============================================================================

// Walk up from bin/Debug/net10.0 until we find the repo root (the dir that has
// assets/branding/cinder-logo.svg). Works whether you run via `dotnet run` or
// the compiled binary.
var repoRoot = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(repoRoot, "assets", "branding", "cinder-logo.svg")))
{
    var parent = Path.GetDirectoryName(repoRoot);
    if (string.IsNullOrEmpty(parent) || parent == repoRoot)
    {
        Console.Error.WriteLine("Could not locate repo root (looking for assets/branding/cinder-logo.svg).");
        return 1;
    }
    repoRoot = parent;
}

var brandingDir = Path.Combine(repoRoot, "assets", "branding");
var pngDir = Path.Combine(brandingDir, "png");
Directory.CreateDirectory(pngDir);

var svgPath = Path.Combine(brandingDir, "cinder-logo.svg");
Console.WriteLine($"Source SVG: {svgPath}");

using var svg = new SKSvg();
if (svg.Load(svgPath) is not { } picture)
{
    Console.Error.WriteLine("Failed to load the source SVG.");
    return 1;
}

// 1. PNG variants. 16/24/32 are the "small list / Explorer thumbnail" sizes;
//    48/64 are Windows tile sizes; 128/256/512 are taskbar / Start menu / Linux
//    .desktop / macOS Dock sizes.
int[] pngSizes = [16, 24, 32, 48, 64, 96, 128, 256, 512];
foreach (var size in pngSizes)
{
    var pngPath = Path.Combine(pngDir, $"cinder-{size}.png");
    RasterizeToPng(picture, size, pngPath, alphaBackground: true);
    Console.WriteLine($"  wrote {Path.GetRelativePath(repoRoot, pngPath)}");
}

// 2. Windows .ico (multi-image).
//    We bundle PNG-compressed images for >= 64px (Windows supports PNG-in-ICO
//    since Vista). The smallest sizes use 32-bpp BMP for maximum compatibility
//    with old shells that don't read PNG-in-ICO.
var icoPath = Path.Combine(brandingDir, "cinder.ico");
WriteIco(picture, [16, 24, 32, 48, 64, 128, 256], icoPath);
Console.WriteLine($"  wrote {Path.GetRelativePath(repoRoot, icoPath)}");

// 3. Hero / splash. 1280×640 with the ember on the left, wordmark on the right.
var heroPath = Path.Combine(brandingDir, "cinder-hero.png");
WriteHero(picture, heroPath);
Console.WriteLine($"  wrote {Path.GetRelativePath(repoRoot, heroPath)}");

Console.WriteLine("Done.");
return 0;

// ----------------------------------------------------------------------------

static void RasterizeToPng(SKPicture picture, int size, string path, bool alphaBackground)
{
    using var bmp = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(bmp))
    {
        canvas.Clear(alphaBackground ? SKColors.Transparent : SKColors.Black);
        var bounds = picture.CullRect;
        var scale = Math.Min(size / bounds.Width, size / bounds.Height);
        canvas.Scale(scale, scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Flush();
    }
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.Create(path);
    data.SaveTo(fs);
}

/// <summary>
/// Writes a multi-image ICO. For 16/24/32/48 we embed 32-bpp BMP (DIB) sub-images
/// because Windows XP / Server 2003 shells need that. For 64/128/256 we embed PNG.
/// </summary>
static void WriteIco(SKPicture picture, int[] sizes, string path)
{
    var entries = new List<(int Size, byte[] Data, bool IsPng)>();
    foreach (var size in sizes)
    {
        using var bmp = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            var bounds = picture.CullRect;
            var scale = Math.Min(size / bounds.Width, size / bounds.Height);
            canvas.Scale(scale, scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();
        }

        if (size >= 64)
        {
            // PNG-compressed: smaller .ico, modern Windows handles it.
            using var img = SKImage.FromBitmap(bmp);
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            entries.Add((size, encoded.ToArray(), IsPng: true));
        }
        else
        {
            // Uncompressed 32-bpp DIB so the icon shows up on every shell.
            entries.Add((size, EncodeBmpForIco(bmp), IsPng: false));
        }
    }

    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);
    // ICONDIR header: reserved (2) + type=1 ICO (2) + count (2)
    bw.Write((ushort)0);
    bw.Write((ushort)1);
    bw.Write((ushort)entries.Count);

    // ICONDIRENTRY: 16 bytes each. Offsets are relative to start of file.
    var offset = 6 + 16 * entries.Count;
    foreach (var e in entries)
    {
        bw.Write((byte)(e.Size == 256 ? 0 : e.Size)); // width  (0 means 256)
        bw.Write((byte)(e.Size == 256 ? 0 : e.Size)); // height (0 means 256)
        bw.Write((byte)0);   // palette count (0 for non-palette)
        bw.Write((byte)0);   // reserved
        bw.Write((ushort)1); // planes
        bw.Write((ushort)32);// bits per pixel
        bw.Write((uint)e.Data.Length);
        bw.Write((uint)offset);
        offset += e.Data.Length;
    }
    foreach (var e in entries)
    {
        bw.Write(e.Data);
    }
}

/// <summary>
/// Encode a 32-bpp top-down BGRA SKBitmap as a DIB (BITMAPINFOHEADER + pixel data
/// rows, bottom-up, with the height field doubled per ICO convention to indicate
/// that the AND mask is appended — we write an all-zero AND mask since alpha
/// already carries transparency).
/// </summary>
static byte[] EncodeBmpForIco(SKBitmap bmp)
{
    var w = bmp.Width;
    var h = bmp.Height;
    var pixelSize = w * h * 4;
    var maskRow = (w + 31) / 32 * 4;
    var maskSize = maskRow * h;
    using var ms = new MemoryStream(40 + pixelSize + maskSize);
    using var bw = new BinaryWriter(ms);
    // BITMAPINFOHEADER (40 bytes)
    bw.Write((uint)40);             // biSize
    bw.Write(w);                    // biWidth
    bw.Write(h * 2);                // biHeight (doubled per ICO spec)
    bw.Write((ushort)1);            // biPlanes
    bw.Write((ushort)32);           // biBitCount
    bw.Write((uint)0);              // biCompression = BI_RGB
    bw.Write((uint)pixelSize);      // biSizeImage
    bw.Write(0);                    // biXPelsPerMeter
    bw.Write(0);                    // biYPelsPerMeter
    bw.Write((uint)0);              // biClrUsed
    bw.Write((uint)0);              // biClrImportant

    // Pixel data, bottom-up.
    for (int y = h - 1; y >= 0; y--)
    {
        for (int x = 0; x < w; x++)
        {
            var c = bmp.GetPixel(x, y);
            bw.Write(c.Blue);
            bw.Write(c.Green);
            bw.Write(c.Red);
            bw.Write(c.Alpha);
        }
    }
    // AND mask — 1 bpp, all zero (means "use alpha channel").
    bw.Write(new byte[maskSize]);
    return ms.ToArray();
}

/// <summary>
/// Splash banner. Dark Cinder Surface0 background, large ember on the left,
/// wordmark + tagline on the right. Used in README + GitHub release.
/// </summary>
static void WriteHero(SKPicture picture, string path)
{
    const int W = 1280;
    const int H = 640;
    using var bmp = new SKBitmap(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(bmp))
    {
        // Background — Cinder Surface0.
        canvas.Clear(new SKColor(0x0B, 0x0C, 0x0F));

        // Subtle ember glow behind the mark.
        using (var glow = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(W * 0.27f, H / 2f),
                260,
                [new SKColor(0xFF, 0x7A, 0x1A, 0x40), new SKColor(0xFF, 0x7A, 0x1A, 0x00)],
                [0f, 1f],
                SKShaderTileMode.Clamp),
        })
        {
            canvas.DrawCircle(W * 0.27f, H / 2f, 280, glow);
        }

        // Ember mark on the left.
        canvas.Save();
        var bounds = picture.CullRect;
        const float emberSize = 360f;
        var scale = emberSize / Math.Max(bounds.Width, bounds.Height);
        canvas.Translate(W * 0.27f - emberSize / 2f, H / 2f - emberSize / 2f);
        canvas.Scale(scale, scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Restore();

        // Wordmark.
        using var titleFont = new SKFont(SKTypeface.FromFamilyName("Inter", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright), 130);
        using var subFont   = new SKFont(SKTypeface.FromFamilyName("Inter", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright), 30);
        using var tagFont   = new SKFont(SKTypeface.FromFamilyName("Cascadia Mono", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright), 18);

        using var titlePaint = new SKPaint { Color = new SKColor(0xEC, 0xED, 0xEE), IsAntialias = true };
        using var subPaint   = new SKPaint { Color = new SKColor(0xFF, 0x7A, 0x1A), IsAntialias = true };
        using var tagPaint   = new SKPaint { Color = new SKColor(0x9C, 0xA0, 0xAB), IsAntialias = true };

        const float textX = 580;
        canvas.DrawText("cinder", textX, H / 2f - 20, SKTextAlign.Left, titleFont, titlePaint);
        canvas.DrawText("Open-source digital-forensics toolkit",
            textX, H / 2f + 30, SKTextAlign.Left, subFont, subPaint);
        canvas.DrawText("what remains tells the story",
            textX, H / 2f + 80, SKTextAlign.Left, tagFont, tagPaint);
        canvas.DrawText("github.com/AffluentMods/cinder",
            textX, H / 2f + 110, SKTextAlign.Left, tagFont, tagPaint);

        canvas.Flush();
    }
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.Create(path);
    data.SaveTo(fs);
}
