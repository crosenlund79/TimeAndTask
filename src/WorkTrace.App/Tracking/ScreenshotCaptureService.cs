using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Common;
using WorkTrace.Core.Screenshots;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;
using WorkTrace.Infrastructure.Tracking;

namespace WorkTrace.App.Tracking;

/// <summary>
/// Automatic screenshots (opt-in, Off by default): silently at the configured interval while tracking runs and the
/// session is unlocked. A capture that looks the same as the previous one is skipped. Old captures are removed
/// after the retention period. Use on the UI thread.
/// </summary>
public sealed class ScreenshotCaptureService(
    ISettingsStore settings,
    IScreenshotService screenshots,
    TrackingEngine engine,
    TimeProvider timeProvider,
    ILogger<ScreenshotCaptureService> logger) : IDisposable
{
    private DispatcherTimer? _timer;
    private DateTime _nextUtc = DateTime.MinValue;
    private DateTime _lastCleanupUtc = DateTime.MinValue;
    private byte[]? _lastSignature;

    public void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    public void Dispose() => _timer?.Stop();

    private async Task TickAsync()
    {
        try
        {
            var s = settings.Current.Screenshots;
            var now = UtcTime.Now(timeProvider);
            if (now - _lastCleanupUtc > TimeSpan.FromHours(6))
            {
                _lastCleanupUtc = now;
                await screenshots.DeleteOlderThanAsync(now.AddDays(-s.RetentionDays));
            }

            var status = engine.Status;
            if (s.Mode == ScreenshotMode.Off || status.State != TrackingState.Tracking || status.IsSessionLocked || now < _nextUtc)
            {
                return;
            }

            _nextUtc = now.AddMinutes(s.CaptureIntervalMinutes);
            await CaptureAsync(s, now);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Automatic screenshot failed");
        }
    }

    private async Task CaptureAsync(ScreenshotSettings s, DateTime now)
    {
        var bounds = s.Mode == ScreenshotMode.AllScreens
            ? System.Windows.Forms.SystemInformation.VirtualScreen
            : System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        // Skip a capture that looks like the previous one (nothing changed on screen).
        var signature = Signature(bitmap);
        if (_lastSignature is not null && Difference(signature, _lastSignature) < 2.0)
        {
            return;
        }

        _lastSignature = signature;
        using var stream = new MemoryStream();
        string extension;
        if (s.Format == ScreenshotFormat.Png)
        {
            bitmap.Save(stream, ImageFormat.Png);
            extension = ".png";
        }
        else
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)s.JpegQuality);
            bitmap.Save(stream, codec, parameters);
            extension = ".jpg";
        }

        await screenshots.SaveAsync(stream.ToArray(), extension, bounds.Width, bounds.Height, now);
    }

    /// <summary>16×16 grayscale thumbnail.</summary>
    private static byte[] Signature(Bitmap bitmap)
    {
        using var small = new Bitmap(16, 16, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(bitmap, 0, 0, 16, 16);
        }

        var result = new byte[256];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var c = small.GetPixel(x, y);
                result[(y * 16) + x] = (byte)((c.R + c.G + c.B) / 3);
            }
        }

        return result;
    }

    private static double Difference(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
}
