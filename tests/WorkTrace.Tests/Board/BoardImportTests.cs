using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Board;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Validation;
using WorkTrace.Tests.Management;

namespace WorkTrace.Tests.Board;

/// <summary>Migration of the web board (docs/board.json on the `board` branch) and its backup ZIPs.</summary>
public class BoardImportTests : ManagementTestBase
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Board", "Fixtures", "web-board.json");

    private IBoardService Board => Services.GetRequiredService<IBoardService>();

    private string TempFile(string name) => Path.Combine(Path.GetTempPath(), "worktrace-tests", $"{Guid.NewGuid():N}-{name}");

    private string WriteJson(JsonNode node)
    {
        var path = TempFile("board.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    private static JsonObject LoadFixture() => JsonNode.Parse(File.ReadAllText(Fixture))!.AsObject();

    [Fact]
    public async Task Import_PreservesIdsStatusesOrderCommentsHistoryLinksAndDependencies()
    {
        var web = LoadFixture();
        var cards = web["cards"]!.AsArray();

        var preview = await Board.PreviewImportAsync(Fixture, Ct);
        Assert.Equal(cards.Count, preview.Count(ImportItemKind.New));
        Assert.Empty(preview.Problems);

        var result = await Board.ImportAsync(Fixture, [], Ct);
        Assert.Equal(cards.Count, result.Added);

        foreach (var node in cards)
        {
            var key = node!["id"]!.GetValue<string>();
            var card = (await Board.GetCardsAsync(new CardFilter(Text: key, Archived: node["archived"]!.GetValue<bool>()), Ct)).Single(c => c.Key == key);
            var detail = await Board.GetCardAsync(card.Id, Ct);
            Assert.Equal(node["title"]!.GetValue<string>(), detail.Title);
            Assert.Equal(node["status"]!.GetValue<string>(), detail.Status.ToString().ToLowerInvariant());
            Assert.Equal(node["comments"]!.AsArray().Count, detail.Comments.Count);
            Assert.Equal(node["history"]!.AsArray().Count + 1, detail.Events.Count); // + the "imported" entry
            Assert.Equal(node["links"]!.AsArray().Count, detail.Links.Count);
            Assert.Equal(node["acceptance"]!.AsArray().Select(a => a!.GetValue<string>()), detail.Acceptance.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal(node["blockedBy"]!.AsArray().Select(b => b!.GetValue<string>()).Order(), detail.Summary.BlockedBy.Select(b => b.Key).Order());
            Assert.Equal(DateTime.Parse(node["createdAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal), detail.Summary.CreatedAtUtc);
        }

        // Column order follows the web board's ranks.
        foreach (var status in BoardRules.Columns)
        {
            var expected = cards.Where(c => c!["status"]!.GetValue<string>() == status.ToString().ToLowerInvariant() && !c["archived"]!.GetValue<bool>())
                .OrderBy(c => c!["rank"]!.GetValue<string>(), StringComparer.Ordinal).Select(c => c!["id"]!.GetValue<string>());
            var actual = (await Board.GetCardsAsync(new CardFilter(Statuses: [status]), Ct)).Select(c => c.Key);
            Assert.Equal(expected, actual);
        }

        // Numbers are never reused: the next card continues after the web board's nextId.
        var next = await Board.CaptureAsync("new", cancellationToken: Ct);
        Assert.Equal(BoardCard.FormatKey(web["nextId"]!.GetValue<int>()), next.Key);
    }

    [Fact]
    public async Task ReImport_IsIdempotent()
    {
        await Board.ImportAsync(Fixture, [], Ct);
        var preview = await Board.PreviewImportAsync(Fixture, Ct);
        Assert.All(preview.Items, i => Assert.Equal(ImportItemKind.Same, i.Kind));

        var result = await Board.ImportAsync(Fixture, [], Ct);
        Assert.Equal((0, 0), (result.Added, result.Updated));
        Assert.Equal(preview.Items.Count, (await Board.GetCardRefsAsync(Ct)).Count);
    }

    [Fact]
    public async Task Import_KeepsLocalEditsUnlessChosen_AndMergesNewComments()
    {
        await Board.ImportAsync(Fixture, [], Ct);
        var web = LoadFixture();
        var webCard = web["cards"]!.AsArray().First(c => c!["id"]!.GetValue<string>() == "WT-010")!;
        var local = (await Board.GetCardsAsync(new CardFilter(Text: "WT-010"), Ct)).Single(c => c.Key == "WT-010");

        Time.Advance(TimeSpan.FromHours(1));
        await Board.UpdateFieldsAsync(local.Id, new CardFields("Edited here", local.Category, local.Priority, null, null, "local notes", null), Ct);
        webCard["title"] = "Edited on the web";
        webCard["comments"]!.AsArray().Add(new JsonObject { ["id"] = "c-new", ["author"] = "claude", ["at"] = "2026-10-29T10:00:00Z", ["text"] = "New finding" });
        var path = WriteJson(web);

        var item = (await Board.PreviewImportAsync(path, Ct)).Items.Single(i => i.Key == "WT-010");
        Assert.Equal(ImportItemKind.LocalNewer, item.Kind);
        Assert.Equal(1, item.NewComments);

        await Board.ImportAsync(path, [], Ct);
        var kept = await Board.GetCardAsync(local.Id, Ct);
        Assert.Equal("Edited here", kept.Title);
        Assert.Contains(kept.Comments, c => c.Text == "New finding");

        await Board.ImportAsync(path, [item.Uid], Ct);
        Assert.Equal("Edited on the web", (await Board.GetCardAsync(local.Id, Ct)).Title);
        Assert.Single((await Board.GetCardAsync(local.Id, Ct)).Comments, c => c.Text == "New finding");
    }

    [Fact]
    public async Task Import_RenumbersCardsWhoseNumberIsTaken()
    {
        var mine = await Board.CreateAsync(new CardFields("Mine", null, BoardPriority.Normal, null, null, null, null), BoardStatus.Todo, Ct);
        Assert.Equal("WT-001", mine.Key);

        var result = await Board.ImportAsync(Fixture, [], Ct);
        var refs = await Board.GetCardRefsAsync(Ct);
        Assert.Equal("Mine", refs.Single(r => r.Key == "WT-001").Title);
        var moved = Assert.Single(refs, r => r.Title.StartsWith("Foundation", StringComparison.Ordinal));
        Assert.Equal("WT-050", moved.Key);
        Assert.Equal(LoadFixture()["cards"]!.AsArray().Count, result.Added);
    }

    [Fact]
    public async Task Import_ReadsBackupZipWithScreenshots_AndRejectsDamagedOnes()
    {
        var web = LoadFixture();
        var card = web["cards"]!.AsArray()[0]!;
        var good = Encoding.ASCII.GetBytes("fake png bytes");
        var bad = Encoding.ASCII.GetBytes("damaged");
        var zipPath = TempFile("backup.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] data)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }

            JsonObject Meta(string id, string path, byte[] data, string hash) => new()
            {
                ["id"] = id, ["cardId"] = card["id"]!.GetValue<string>(), ["cardUid"] = card["uid"]!.GetValue<string>(), ["path"] = path,
                ["name"] = "shot.png", ["type"] = "image/png", ["bytes"] = data.Length, ["caption"] = "Login", ["createdAt"] = "2026-10-29T10:00:00Z", ["sha256"] = hash,
            };

            var manifest = new JsonObject
            {
                ["format"] = "worktrace-board-backup", ["formatVersion"] = 1,
                ["attachments"] = new JsonArray(
                    Meta("att-1", "attachments/WT-001/att-1-shot.png", good, Convert.ToHexStringLower(SHA256.HashData(good))),
                    Meta("att-2", "attachments/WT-001/att-2-shot.png", bad, new string('0', 64))),
            };
            Add("manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            Add("cards.json", Encoding.UTF8.GetBytes(web.ToJsonString()));
            Add("attachments/WT-001/att-1-shot.png", good);
            Add("attachments/WT-001/att-2-shot.png", bad);
        }

        var preview = await Board.PreviewImportAsync(zipPath, Ct);
        Assert.Equal(1, preview.AttachmentCount);
        Assert.Contains(preview.Problems, p => p.Contains("damaged", StringComparison.Ordinal));

        var result = await Board.ImportAsync(zipPath, [], Ct);
        Assert.Equal(1, result.AttachmentsAdded);
        var id = (await Board.GetCardRefsAsync(Ct)).Single(r => r.Key == "WT-001").Id;
        var attachment = Assert.Single((await Board.GetCardAsync(id, Ct)).Attachments);
        Assert.Equal(("Login", "att-1"), (attachment.Caption, attachment.Uid));
        Assert.Equal(good, await File.ReadAllBytesAsync(attachment.FullPath, Ct));

        // Importing again adds nothing.
        Assert.Equal(0, (await Board.ImportAsync(zipPath, [], Ct)).AttachmentsAdded);
    }

    [Fact]
    public async Task Import_RejectsInvalidFiles_WithoutChangingTheBoard()
    {
        var notJson = TempFile("x.json");
        Directory.CreateDirectory(Path.GetDirectoryName(notJson)!);
        await File.WriteAllTextAsync(notJson, "hello", Ct);
        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Board.PreviewImportAsync(notJson, Ct));

        var web = LoadFixture();
        web["cards"]!.AsArray()[1]!["id"] = "WT-001"; // duplicate id
        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Board.ImportAsync(WriteJson(web), [], Ct));

        var cycle = LoadFixture();
        var cards = cycle["cards"]!.AsArray();
        cards.First(c => c!["id"]!.GetValue<string>() == "WT-010")!["blockedBy"] = new JsonArray("WT-030");
        var result = await Board.ImportAsync(WriteJson(cycle), [], Ct);
        Assert.Contains(result.Notes, n => n.Contains("cycle", StringComparison.Ordinal));
        Assert.Equal(cards.Count, result.Added);

        _ = JsonSerializer.Serialize(result);
    }
}
