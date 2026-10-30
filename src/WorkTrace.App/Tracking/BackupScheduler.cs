using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Tray;
using WorkTrace.Core.Board;

namespace WorkTrace.App.Tracking;

/// <summary>Runs due automatic backups a few minutes after start and every 30 minutes (off the UI thread).</summary>
internal sealed class BackupScheduler(IAutomaticBackupService backups, TrayIconService tray, ILogger<BackupScheduler> logger) : IDisposable
{
    private DispatcherTimer? _timer;
    private bool _running;

    public void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromMinutes(30);
            await RunAsync();
        };
        _timer.Start();
    }

    public void Dispose() => _timer?.Stop();

    private async Task RunAsync()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        try
        {
            var results = await Task.Run(() => backups.RunDueAsync());
            foreach (var failed in results.Where(r => r.Ran && !r.Succeeded))
            {
                tray.ShowNotification(failed.Destination == BackupDestination.Folder ? "Backup to folder failed" : "Email backup failed", failed.Message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled backup failed");
        }
        finally
        {
            _running = false;
        }
    }
}
