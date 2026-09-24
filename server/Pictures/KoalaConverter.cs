using SkiaSharp;

namespace Wic64Server.Pictures;

/// <summary>
/// Converts any image (PNG, JPG, GIF, WebP, ...) into a C64 multicolor bitmap in Koala layout:
/// 8000 bytes bitmap, 1000 bytes screen RAM, 1000 bytes color RAM and 1 byte background color.
/// </summary>
/// <remarks>
/// Multicolor bitmaps are 160x200 "fat" pixels. Every 4x8 cell can use the shared background
/// color plus 3 colors of its own. We pick the background color first, then the best 3 colors
/// per cell (brute force over all 455 combinations), and finally Floyd-Steinberg dither the
/// image while each pixel is restricted to the 4 colors of its cell.
/// </remarks>
public static class KoalaConverter
{
    const int Width = 160;
    const int Height = 200;
    const float DitherStrength = 0.75f;

    // "Pepto" PAL palette
    static readonly (float R, float G, float B)[] Palette =
    [
        (0x00, 0x00, 0x00), (0xff, 0xff, 0xff), (0x68, 0x37, 0x2b), (0x70, 0xa4, 0xb2),
        (0x6f, 0x3d, 0x86), (0x58, 0x8d, 0x43), (0x35, 0x28, 0x79), (0xb8, 0xc7, 0x6f),
        (0x6f, 0x4f, 0x25), (0x43, 0x39, 0x00), (0x9a, 0x67, 0x59), (0x44, 0x44, 0x44),
        (0x6c, 0x6c, 0x6c), (0x9a, 0xd2, 0x84), (0x6c, 0x5e, 0xb5), (0x95, 0x95, 0x95),
    ];

    public static (byte R, byte G, byte B) PaletteColor(int index)
    {
        var (r, g, b) = Palette[index];
        return ((byte)r, (byte)g, (byte)b);
    }

    public static byte[] Convert(string path)
    {
        var pixels = LoadAndScale(path);

        var background = PickBackground(pixels);
        var cellColors = new int[1000][];
        for (var cell = 0; cell < 1000; cell++)
            cellColors[cell] = PickCellColors(pixels, cell, background);

        // Koala layout: bitmap at 0, screen RAM at 8000, color RAM at 9000, background at 10000
        var koala = new byte[10001];
        koala[10000] = (byte)background;

        for (var cell = 0; cell < 1000; cell++)
        {
            var c = cellColors[cell];
            koala[8000 + cell] = (byte)(c[1] << 4 | c[2]); // bit pattern 01 = high nibble, 10 = low nibble
            koala[9000 + cell] = (byte)c[3];               // bit pattern 11 = color RAM
        }

        Dither(pixels, cellColors, (x, y, bits) =>
        {
            var cell = y / 8 * 40 + x / 4;
            koala[cell * 8 + y % 8] |= (byte)(bits << (6 - 2 * (x % 4)));
        });

        return koala;
    }

    static (float R, float G, float B)[,] LoadAndScale(string path)
    {
        using var source = SKBitmap.Decode(path) ?? throw new InvalidDataException("unsupported image");
        using var image = SKImage.FromBitmap(source);

        // Fit the image into the 320x200 display, then squash it horizontally into 160 fat pixels
        var scale = Math.Min(320.0 / source.Width, 200.0 / source.Height);
        var width = (float)(source.Width * scale / 2);
        var height = (float)(source.Height * scale);

        using var target = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.Black);
            var rect = SKRect.Create((Width - width) / 2, (Height - height) / 2, width, height);
            canvas.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        var pixels = new (float, float, float)[Height, Width];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var p = target.GetPixel(x, y);
            pixels[y, x] = (p.Red, p.Green, p.Blue);
        }

        return pixels;
    }

    /// <summary>The color that is nearest to the most pixels becomes the shared background color.</summary>
    static int PickBackground((float R, float G, float B)[,] pixels)
    {
        var counts = new int[16];
        foreach (var p in pixels)
            counts[Nearest(p, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]).Color]++;
        return Array.IndexOf(counts, counts.Max());
    }

    /// <summary>Finds the 3 colors that, together with the background, best match the cell's 32 pixels.</summary>
    static int[] PickCellColors((float R, float G, float B)[,] pixels, int cell, int background)
    {
        var cx = cell % 40 * 4;
        var cy = cell / 40 * 8;

        var distances = new float[32, 16];
        for (var i = 0; i < 32; i++)
        for (var c = 0; c < 16; c++)
            distances[i, c] = Distance(pixels[cy + i / 4, cx + i % 4], Palette[c]);

        var best = new[] { background, background, background, background };
        var bestError = float.MaxValue;

        for (var a = 0; a < 16; a++)
        for (var b = a + 1; b < 16; b++)
        for (var c = b + 1; c < 16; c++)
        {
            if (a == background || b == background || c == background)
                continue;

            var error = 0f;
            for (var i = 0; i < 32 && error < bestError; i++)
                error += Math.Min(Math.Min(distances[i, background], distances[i, a]), Math.Min(distances[i, b], distances[i, c]));

            if (error < bestError)
            {
                bestError = error;
                best = [background, a, b, c];
            }
        }

        return best;
    }

    /// <summary>Serpentine Floyd-Steinberg dithering, limited to the 4 colors of each cell.</summary>
    static void Dither((float R, float G, float B)[,] source, int[][] cellColors, Action<int, int, int> plot)
    {
        var work = (ValueTuple<float, float, float>[,])source.Clone();

        for (var y = 0; y < Height; y++)
        {
            var leftToRight = y % 2 == 0;
            for (var i = 0; i < Width; i++)
            {
                var x = leftToRight ? i : Width - 1 - i;
                var colors = cellColors[y / 8 * 40 + x / 4];

                var (r, g, b) = work[y, x];
                var (index, color) = Nearest((r, g, b), colors);
                plot(x, y, index);

                var target = Palette[color];
                var error = ((r - target.R) * DitherStrength, (g - target.G) * DitherStrength, (b - target.B) * DitherStrength);

                var forward = leftToRight ? 1 : -1;
                Spread(work, x + forward, y, error, 7 / 16f);
                Spread(work, x - forward, y + 1, error, 3 / 16f);
                Spread(work, x, y + 1, error, 5 / 16f);
                Spread(work, x + forward, y + 1, error, 1 / 16f);
            }
        }
    }

    static void Spread((float R, float G, float B)[,] work, int x, int y, (float R, float G, float B) error, float factor)
    {
        if (x < 0 || x >= Width || y >= Height)
            return;

        var (r, g, b) = work[y, x];
        work[y, x] = (
            Math.Clamp(r + error.R * factor, -64, 320),
            Math.Clamp(g + error.G * factor, -64, 320),
            Math.Clamp(b + error.B * factor, -64, 320));
    }

    static (int Index, int Color) Nearest((float R, float G, float B) pixel, int[] colors)
    {
        var bestIndex = 0;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < colors.Length; i++)
        {
            var d = Distance(pixel, Palette[colors[i]]);
            if (d < bestDistance)
            {
                bestDistance = d;
                bestIndex = i;
            }
        }

        return (bestIndex, colors[bestIndex]);
    }

    /// <summary>"Redmean" weighted RGB distance, a cheap approximation of perceived color difference.</summary>
    static float Distance((float R, float G, float B) a, (float R, float G, float B) b)
    {
        var mean = (a.R + b.R) / 2;
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return (2 + mean / 256) * dr * dr + 4 * dg * dg + (2 + (255 - mean) / 256) * db * db;
    }
}
