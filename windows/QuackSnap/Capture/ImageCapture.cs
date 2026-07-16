using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using QuackSnap.Core;

namespace QuackSnap.Capture;

public sealed record CapturedImage(byte[] PngBytes, string PixelHash, int Width, int Height);

/// <summary>
/// Reads images off the clipboard or disk into PNG bytes, and hashes decoded
/// pixels so the same capture arriving twice (clipboard + Screenshots folder)
/// is only sent once regardless of encoder differences.
/// </summary>
public static class ImageCapture
{
    /// <summary>Must run on the UI thread. Retries because other apps briefly lock the clipboard.</summary>
    public static CapturedImage? FromClipboard()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return null;

                // Prefer the source's own PNG bytes (keeps exact encoding, cheap).
                if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream pngStream)
                {
                    var png = pngStream.ToArray();
                    using var bmp = new Bitmap(new MemoryStream(png));
                    return new CapturedImage(png, PixelHash(bmp), bmp.Width, bmp.Height);
                }

                if (data.GetDataPresent(DataFormats.Bitmap) && Clipboard.GetImage() is Bitmap image)
                {
                    using (image)
                        return Encode(image);
                }

                return null;
            }
            catch (ExternalException)
            {
                Thread.Sleep(100); // clipboard busy — retry
            }
            catch (Exception ex)
            {
                Logger.Error("Reading clipboard image failed", ex);
                return null;
            }
        }
        return null;
    }

    public static CapturedImage? FromFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var bmp = new Bitmap(new MemoryStream(bytes));
            bool isPng = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P';
            return isPng
                ? new CapturedImage(bytes, PixelHash(bmp), bmp.Width, bmp.Height)
                : Encode(bmp);
        }
        catch (Exception ex)
        {
            Logger.Error($"Reading image file failed: {path}", ex);
            return null;
        }
    }

    private static CapturedImage Encode(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return new CapturedImage(ms.ToArray(), PixelHash(bmp), bmp.Width, bmp.Height);
    }

    /// <summary>Optional lossy re-encode for the "compress screenshots" setting.</summary>
    public static byte[] ToJpeg(byte[] pngBytes, long quality = 82)
    {
        using var bmp = new Bitmap(new MemoryStream(pngBytes));
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, codec, parameters);
        return ms.ToArray();
    }

    private static string PixelHash(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bmp.Width * 4;
            var row = new byte[rowBytes];
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, rowBytes);
                sha.AppendData(row);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}

/// <summary>Remembers recently seen pixel hashes so duplicate captures are dropped.</summary>
public sealed class Deduper
{
    private readonly TimeSpan _window = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, DateTime> _seen = new();

    public bool IsNew(string pixelHash)
    {
        var now = DateTime.UtcNow;
        foreach (var stale in _seen.Where(kv => now - kv.Value > _window).Select(kv => kv.Key).ToList())
            _seen.Remove(stale);

        if (_seen.ContainsKey(pixelHash)) return false;
        _seen[pixelHash] = now;
        return true;
    }
}
