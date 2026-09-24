using System.Collections.Concurrent;
using SkiaSharp;

namespace Wic64Server.Pictures;

/// <summary>Pictures as Koala data for the C64 (cached), and as PNG previews for the web UI.</summary>
public sealed class PictureService(ILogger<PictureService> logger)
{
    readonly ConcurrentDictionary<(string Path, DateTime Modified), byte[]> cache = new();

    /// <summary>10001 bytes: bitmap, screen RAM, color RAM, background color.</summary>
    public byte[] Koala(string path)
    {
        var info = new FileInfo(path);
        return cache.GetOrAdd((info.FullName, info.LastWriteTimeUtc), key =>
        {
            var ext = Path.GetExtension(key.Path).ToLowerInvariant();
            if (ext is ".koa" or ".kla")
                return File.ReadAllBytes(key.Path).AsSpan(2, 10001).ToArray(); // skip load address

            var started = DateTime.UtcNow;
            var result = KoalaConverter.Convert(key.Path);
            logger.LogInformation("Converted {File} in {Ms} ms", info.Name, (int)(DateTime.UtcNow - started).TotalMilliseconds);
            return result;
        });
    }

    /// <summary>The picture as the C64 shows it, as a 320x200 PNG (multicolor pixels are two pixels wide).</summary>
    public byte[] PreviewPng(string path)
    {
        var koala = Koala(path);
        using var bitmap = new SKBitmap(320, 200, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 160; x++)
        {
            var cell = y / 8 * 40 + x / 4;
            var bits = koala[cell * 8 + y % 8] >> (6 - 2 * (x % 4)) & 3;
            var color = bits switch
            {
                0 => koala[10000],
                1 => koala[8000 + cell] >> 4,
                2 => koala[8000 + cell] & 15,
                _ => koala[9000 + cell] & 15,
            };
            var (r, g, b) = KoalaConverter.PaletteColor(color & 15);
            var pixel = new SKColor(r, g, b);
            bitmap.SetPixel(x * 2, y, pixel);
            bitmap.SetPixel(x * 2 + 1, y, pixel);
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
