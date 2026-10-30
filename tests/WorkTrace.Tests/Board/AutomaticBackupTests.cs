using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Board;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Settings;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Tests.Board;

public sealed class AutomaticBackupTests : IAsyncLifetime
{
    private readonly TestDataDirectory _data = new();
    private readonly FakeMailer _mailer = new();
    private ServiceProvider _services = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public async ValueTask InitializeAsync()
    {
        _services = _data.BuildServices(s => s.AddSingleton<IBackupMailer>(_mailer));
        await Get<DatabaseInitializer>().InitializeAsync(Ct);
        await Get<ISettingsStore>().LoadAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _data.Dispose();
    }

    [Fact]
    public async Task Folder_WritesCompleteAndMonthlyBackups_AndSkipsWhenNothingChanged()
    {
        var folder = Path.Combine(_data.Root, "OneDrive", "WorkTrace");
        await Get<ISettingsStore>().UpdateAsync(s => (s.Backup.FolderEnabled, s.Backup.Folder) = (true, folder), Ct);
        await Get<IBoardService>().CaptureAsync("A thought", cancellationToken: Ct);

        var first = Assert.Single(await Get<IAutomaticBackupService>().RunDueAsync(Ct));
        Assert.True(first.Succeeded, first.Message);
        Assert.Single(Directory.GetFiles(folder, "WorkTrace-complete-*.zip"));
        Assert.Single(Directory.GetFiles(folder, "WorkTrace-board-*.zip"));
        Assert.NotNull(Get<ISettingsStore>().Current.Backup.LastFolderUtc);

        var forced = await Get<IAutomaticBackupService>().RunNowAsync(BackupDestination.Folder, Ct);
        Assert.True(forced.Ran);
        _data.Time.Advance(TimeSpan.FromDays(1));
        Assert.False(Assert.Single(await Get<IAutomaticBackupService>().RunDueAsync(Ct)).Ran); // due, but nothing changed
    }

    [Fact]
    public async Task Email_SendsDatabaseAndChangedMonths_SplitsLargeMonths_AndNeverResendsUnchanged()
    {
        await Get<ISettingsStore>().UpdateAsync(s =>
        {
            s.Backup.EmailEnabled = true;
            s.Backup.EmailTo = "me@example.com";
            s.Backup.EmailMaxMegabytes = 1;
        }, Ct);
        var board = Get<IBoardService>();
        for (var i = 0; i < 3; i++)
        {
            var card = await board.CaptureAsync($"Card {i}", cancellationToken: Ct);
            var noise = new byte[400 * 1024];
            Random.Shared.NextBytes(noise); // incompressible
            await board.AddAttachmentAsync(card.Id, "shot.png", "image/png", noise, cancellationToken: Ct);
        }

        var result = await Get<IAutomaticBackupService>().RunNowAsync(BackupDestination.Email, Ct);
        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(_mailer.Sent, m => m.Subject.Contains("database", StringComparison.Ordinal));
        var parts = _mailer.Sent.Where(m => m.Subject.Contains("part", StringComparison.Ordinal)).ToList();
        Assert.True(parts.Count >= 2, string.Join(", ", _mailer.Sent.Select(m => m.Subject)));
        Assert.All(_mailer.Sent, m => Assert.True(m.Bytes <= 1024 * 1024));

        _mailer.Sent.Clear();
        _data.Time.Advance(TimeSpan.FromDays(1));
        await Get<IAutomaticBackupService>().RunDueAsync(Ct);
        Assert.Empty(_mailer.Sent);
    }

    private sealed class FakeMailer : IBackupMailer
    {
        public List<(string Subject, int Bytes)> Sent { get; } = [];

        public Task SendAsync(string sender, string recipient, string subject, string body, string attachmentName, byte[] attachment, CancellationToken cancellationToken = default)
        {
            Sent.Add((subject, attachment.Length));
            return Task.CompletedTask;
        }
    }
}
