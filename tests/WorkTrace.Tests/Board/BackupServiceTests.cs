using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Board;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Validation;
using WorkTrace.Infrastructure.Persistence;
using WorkTrace.Tests.Management;

namespace WorkTrace.Tests.Board;

public class BackupServiceTests : ManagementTestBase
{
    private IBoardService Board => Services.GetRequiredService<IBoardService>();

    private IBackupService Backups => Services.GetRequiredService<IBackupService>();

    private static string TempZip() => Path.Combine(Path.GetTempPath(), "worktrace-tests", $"{Guid.NewGuid():N}.zip");

    private async Task<(CardDetail A, CardDetail B)> SeedAsync()
    {
        var a = await Board.CreateAsync(new CardFields("Card A", BoardCategory.Bug, BoardPriority.High, "desc", "one\ntwo", "notes", "ev"), BoardStatus.Doing, Ct);
        var b = await Board.CreateAsync(new CardFields("Card B", null, BoardPriority.Normal, null, null, null, null), BoardStatus.Todo, Ct);
        await Board.SetBlockedByAsync(a.Id, [b.Id], Ct);
        await Board.SetLinksAsync(a.Id, [new LinkInput("Spec", "https://example.com/spec")], Ct);
        await Board.AddCommentAsync(a.Id, "A comment", Ct);
        await Board.AddAttachmentAsync(a.Id, "shot.png", "image/png", [1, 2, 3, 4], "Caption", Ct);
        return (a, b);
    }

    [Fact]
    public async Task CompleteBackup_ContainsCardsScreenshotsDatabaseAndSettings_AndRestoresIntoAnEmptyBoard()
    {
        var (a, _) = await SeedAsync();
        var zip = TempZip();

        var result = await Backups.CreateAsync(new BackupRequest(BackupKind.Complete), zip, Ct);

        Assert.True(result.IncludesDatabase);
        Assert.Equal((2, 1), (result.CardCount, result.AttachmentCount));
        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("README.txt", names);
            Assert.Contains("manifest.json", names);
            Assert.Contains("cards.json", names);
            Assert.Contains("cards.md", names);
            Assert.Contains("database/worktrace.db", names);
            Assert.Contains(names, n => n.StartsWith("attachments/WT-001/", StringComparison.Ordinal));
        }

        // Restore into a fresh data directory: everything comes back, numbers and ids preserved.
        using var other = new TestDataDirectory();
        await using var services = other.BuildServices();
        await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(Ct);
        var target = services.GetRequiredService<IBoardService>();
        var preview = await target.PreviewImportAsync(zip, Ct);
        Assert.True(preview.IncludesDatabase);
        Assert.Equal(2, preview.Count(ImportItemKind.New));
        await target.ImportAsync(zip, [], Ct);

        var restored = await target.GetCardAsync((await target.GetCardRefsAsync(Ct)).Single(r => r.Key == a.Key).Id, Ct);
        var original = await Board.GetCardAsync(a.Id, Ct);
        Assert.Equal((original.Title, original.Description, original.Acceptance, original.Notes, original.Evidence),
            (restored.Title, restored.Description, restored.Acceptance, restored.Notes, restored.Evidence));
        Assert.Equal(original.Links, restored.Links);
        Assert.Equal("WT-002", Assert.Single(restored.Summary.BlockedBy).Key);
        Assert.Equal(original.Comments.Select(c => c.Text), restored.Comments.Select(c => c.Text));
        var attachment = Assert.Single(restored.Attachments);
        Assert.Equal("Caption", attachment.Caption);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(attachment.FullPath, Ct));
        Assert.Equal(BoardStatus.Doing, restored.Status);

        // Restoring the same backup again changes nothing.
        Assert.All((await target.PreviewImportAsync(zip, Ct)).Items, i => Assert.Equal(ImportItemKind.Same, i.Kind));
        await services.DisposeAsync();
    }

    [Fact]
    public async Task Months_TrackChangesSinceTheirLastBackup()
    {
        await SeedAsync();
        Time.Advance(TimeSpan.FromDays(40));
        var later = await Board.CaptureAsync("Next month", cancellationToken: Ct);

        var months = await Backups.GetMonthsAsync(cancellationToken: Ct);
        Assert.Equal(["2026-12", "2026-10"], months.Select(m => m.Month));
        Assert.All(months, m => Assert.True(m.ChangedSinceBackup));

        await Backups.CreateAsync(new BackupRequest(BackupKind.Month, "2026-10"), TempZip(), Ct);
        months = await Backups.GetMonthsAsync(cancellationToken: Ct);
        Assert.False(months.Single(m => m.Month == "2026-10").ChangedSinceBackup);
        Assert.True(months.Single(m => m.Month == "2026-12").ChangedSinceBackup);

        await Backups.CreateAsync(new BackupRequest(BackupKind.AllCards), TempZip(), Ct);
        Assert.All(await Backups.GetMonthsAsync(cancellationToken: Ct), m => Assert.False(m.ChangedSinceBackup));

        await Board.AddCommentAsync(later.Id, "changed", Ct);
        months = await Backups.GetMonthsAsync(cancellationToken: Ct);
        Assert.True(months.Single(m => m.Month == "2026-12").ChangedSinceBackup);
        Assert.False(months.Single(m => m.Month == "2026-10").ChangedSinceBackup);
    }

    [Fact]
    public async Task SelectedCards_ExportOnlyThose_AndManifestMatchesTheWebFormat()
    {
        var (_, b) = await SeedAsync();
        var zip = TempZip();
        await Backups.CreateAsync(new BackupRequest(BackupKind.Cards, CardIds: [b.Id]), zip, Ct);

        using var archive = ZipFile.OpenRead(zip);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Assert.Equal("worktrace-board-backup", manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal("WT-002", manifest.RootElement.GetProperty("cards")[0].GetProperty("id").GetString());
        Assert.Equal(0, manifest.RootElement.GetProperty("attachments").GetArrayLength());
        using var cards = JsonDocument.Parse(archive.GetEntry("cards.json")!.Open());
        Assert.Equal(2, cards.RootElement.GetProperty("schemaVersion").GetInt32());
        var card = Assert.Single(cards.RootElement.GetProperty("cards").EnumerateArray());
        Assert.Equal("todo", card.GetProperty("status").GetString());
        Assert.Matches("^[0-9a-z]*[1-9a-z]$", card.GetProperty("rank").GetString());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("category").ValueKind);
    }

    [Fact]
    public async Task Restore_RejectsADamagedArchive()
    {
        await SeedAsync();
        var zip = TempZip();
        await Backups.CreateAsync(new BackupRequest(BackupKind.AllCards), zip, Ct);
        var bytes = await File.ReadAllBytesAsync(zip, Ct);
        await File.WriteAllBytesAsync(zip, bytes[..(bytes.Length / 2)], Ct);

        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Board.PreviewImportAsync(zip, Ct));
    }

    [Fact]
    public async Task Backup_RefusesAnEmptySelection()
    {
        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Backups.CreateAsync(new BackupRequest(BackupKind.Month, "2020-01"), TempZip(), Ct));
    }
}
