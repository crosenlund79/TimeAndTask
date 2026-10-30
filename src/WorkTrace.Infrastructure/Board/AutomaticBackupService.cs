using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Board;
using WorkTrace.Core.Common;
using WorkTrace.Core.Settings;
using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.Infrastructure.Board;

/// <summary>Sends email through the Resend API (https://resend.com). The key comes from <see cref="ISecretStore"/>.</summary>
internal sealed class ResendMailer(ISecretStore secrets) : IBackupMailer
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task SendAsync(string sender, string recipient, string subject, string body, string attachmentName, byte[] attachment, CancellationToken cancellationToken = default)
    {
        var key = secrets.Read(IAutomaticBackupService.EmailKeySecret)
            ?? throw new InvalidOperationException("No email API key is stored. Add your Resend API key under Settings › Backup.");
        var payload = JsonSerializer.Serialize(new
        {
            from = sender,
            to = recipient.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            subject,
            text = body,
            attachments = attachment.Length == 0 ? null : new[] { new { filename = attachmentName, content = Convert.ToBase64String(attachment) } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Resend's error text explains the problem (bad key, unverified sender…); it never contains the key.
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Email service refused the message (HTTP {(int)response.StatusCode}): {Trim(detail)}");
        }
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;
}

/// <summary>
/// Scheduled backups. Folder: a complete backup (database, settings, card attachments, guide files and, optionally,
/// screenshots) plus one ZIP per changed creation month, with older complete backups pruned. Email: the database
/// backup when data changed, plus one email per changed creation month (split into several restorable parts when
/// too big; a card too big on its own is reported, never dropped silently). Months are tracked per destination.
/// </summary>
internal sealed class AutomaticBackupService(
    IBackupService backups,
    IBackupMailer mailer,
    ISettingsStore settings,
    AppPaths paths,
    TimeProvider timeProvider,
    ILogger<AutomaticBackupService> logger) : IAutomaticBackupService, IDisposable
{
    private const string FolderScope = "folder-month:";
    private const string EmailScope = "email-month:";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<BackupRunResult>> RunDueAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<BackupRunResult>();
        var s = settings.Current.Backup;
        if (s.FolderEnabled && IsDue(s.LastFolderUtc, s.Frequency))
        {
            results.Add(await RunAsync(BackupDestination.Folder, force: false, cancellationToken));
        }

        if (s.EmailEnabled && IsDue(s.LastEmailUtc, s.Frequency))
        {
            results.Add(await RunAsync(BackupDestination.Email, force: false, cancellationToken));
        }

        return results;
    }

    public void Dispose() => _gate.Dispose();

    public Task<BackupRunResult> RunNowAsync(BackupDestination destination, CancellationToken cancellationToken = default) =>
        RunAsync(destination, force: true, cancellationToken);

    private bool IsDue(DateTime? lastUtc, BackupFrequency frequency)
    {
        if (lastUtc is not { } last)
        {
            return true;
        }

        var now = UtcTime.Now(timeProvider);
        return frequency == BackupFrequency.Weekly
            ? now - last >= TimeSpan.FromDays(7)
            : TimeZoneInfo.ConvertTimeFromUtc(last, TimeZoneInfo.Local).Date < TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local).Date;
    }

    private async Task<BackupRunResult> RunAsync(BackupDestination destination, bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fingerprint = await backups.GetDataFingerprintAsync(cancellationToken);
            var s = settings.Current.Backup;
            var last = destination == BackupDestination.Folder ? s.LastFolderFingerprint : s.LastEmailFingerprint;
            if (!force && fingerprint == last)
            {
                return new BackupRunResult(destination, false, true, "Nothing changed since the last backup.");
            }

            try
            {
                var message = destination == BackupDestination.Folder
                    ? await BackupToFolderAsync(s, cancellationToken)
                    : await BackupByEmailAsync(s, fingerprint != s.LastEmailFingerprint || force, cancellationToken);
                await settings.UpdateAsync(x =>
                {
                    var now = UtcTime.Now(timeProvider);
                    if (destination == BackupDestination.Folder)
                    {
                        (x.Backup.LastFolderUtc, x.Backup.LastFolderResult, x.Backup.LastFolderError, x.Backup.LastFolderFingerprint) = (now, message, null, fingerprint);
                    }
                    else
                    {
                        (x.Backup.LastEmailUtc, x.Backup.LastEmailResult, x.Backup.LastEmailError, x.Backup.LastEmailFingerprint) = (now, message, null, fingerprint);
                    }
                }, cancellationToken);
                logger.LogInformation("Automatic backup ({Destination}) succeeded", destination);
                return new BackupRunResult(destination, true, true, message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Automatic backup ({Destination}) failed", destination);
                var error = $"{DateTime.Now:g}: {ex.Message}";
                await settings.UpdateAsync(x =>
                {
                    if (destination == BackupDestination.Folder)
                    {
                        x.Backup.LastFolderError = error;
                    }
                    else
                    {
                        x.Backup.LastEmailError = error;
                    }
                }, cancellationToken);
                return new BackupRunResult(destination, true, false, ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> BackupToFolderAsync(BackupSettings s, CancellationToken cancellationToken)
    {
        var folder = string.IsNullOrWhiteSpace(s.Folder) ? throw new InvalidOperationException("Choose a backup folder first.") : s.Folder;
        Directory.CreateDirectory(folder);
        var day = TimeZoneInfo.ConvertTimeFromUtc(UtcTime.Now(timeProvider), TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var complete = await backups.CreateAsync(
            new BackupRequest(BackupKind.Complete, IncludeScreenshots: s.FolderIncludeScreenshots, RecordAs: ""),
            Path.Combine(folder, $"WorkTrace-complete-{day}.zip"),
            cancellationToken);
        var files = 1;
        var bytes = complete.SizeBytes;

        // One file per creation month, rewritten when anything in that month changed (older cards included).
        foreach (var month in (await backups.GetMonthsAsync(FolderScope, cancellationToken)).Where(m => m.ChangedSinceBackup))
        {
            var result = await backups.CreateAsync(
                new BackupRequest(BackupKind.Month, month.Month, RecordAs: FolderScope), Path.Combine(folder, $"WorkTrace-board-{month.Month}.zip"), cancellationToken);
            files++;
            bytes += result.SizeBytes;
        }

        // Keep the newest complete backups only (by the date in the name).
        foreach (var old in Directory.EnumerateFiles(folder, "WorkTrace-complete-*.zip").OrderByDescending(f => f, StringComparer.Ordinal).Skip(s.FolderKeep))
        {
            File.Delete(old);
        }

        return $"{files} file(s), {bytes / (1024.0 * 1024):0.0} MB written to {folder}";
    }

    private async Task<string> BackupByEmailAsync(BackupSettings s, bool sendDatabase, CancellationToken cancellationToken)
    {
        var to = string.IsNullOrWhiteSpace(s.EmailTo) ? throw new InvalidOperationException("Enter the email address to send backups to.") : s.EmailTo;
        var limit = s.EmailMaxMegabytes * 1024L * 1024;
        var temp = Path.Combine(paths.BackupsDirectory, "email-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var sent = new List<string>();
        var problems = new List<string>();
        try
        {
            var day = TimeZoneInfo.ConvertTimeFromUtc(UtcTime.Now(timeProvider), TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (sendDatabase)
            {
                var name = $"WorkTrace-database-{day}.zip";
                var file = await backups.CreateAsync(new BackupRequest(BackupKind.Database), Path.Combine(temp, name), cancellationToken);
                if (file.SizeBytes > limit)
                {
                    problems.Add($"The database backup is {Mb(file.SizeBytes)}, over the {s.EmailMaxMegabytes} MB email limit: use the folder backup for it.");
                }
                else
                {
                    await SendAsync(s, to, $"WorkTrace backup {day}: database", "The WorkTrace database (time registrations, tasks, stakeholders, board, guides), settings and guide files.", name, file.Path, cancellationToken);
                    sent.Add("database");
                }
            }

            foreach (var month in (await backups.GetMonthsAsync(EmailScope, cancellationToken)).Where(m => m.ChangedSinceBackup))
            {
                var name = $"WorkTrace-board-{month.Month}.zip";
                var file = await backups.CreateAsync(new BackupRequest(BackupKind.Month, month.Month, RecordAs: ""), Path.Combine(temp, name), cancellationToken);
                var problemsBefore = problems.Count;
                if (file.SizeBytes <= limit)
                {
                    await SendAsync(s, to, $"WorkTrace backup: cards created {month.Month}", MonthBody(month.Month, null), name, file.Path, cancellationToken);
                }
                else
                {
                    // Too big for one email: several emails, each a complete, restorable backup of some of the month's cards.
                    var parts = Split(await backups.GetMonthCardsAsync(month.Month, cancellationToken), limit, problems, month.Month);
                    for (var i = 0; i < parts.Count; i++)
                    {
                        var partName = $"WorkTrace-board-{month.Month}-part{i + 1}of{parts.Count}.zip";
                        var part = await backups.CreateAsync(new BackupRequest(BackupKind.Cards, CardIds: parts[i], RecordAs: ""), Path.Combine(temp, partName), cancellationToken);
                        await SendAsync(s, to, $"WorkTrace backup: cards created {month.Month} (part {i + 1} of {parts.Count})", MonthBody(month.Month, (i + 1, parts.Count)),
                            partName, part.Path, cancellationToken);
                    }

                }

                // A month with an unsendable card stays "changed", so the error keeps showing until it is resolved.
                if (problems.Count == problemsBefore)
                {
                    await backups.RecordMonthAsync(month.Month, month.ContentHash, EmailScope, name, cancellationToken);
                }

                sent.Add(month.Month);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException((sent.Count > 0 ? $"Sent {string.Join(", ", sent)}. " : "") + string.Join(" ", problems));
        }

        return sent.Count == 0 ? "Nothing new to send." : $"Sent {sent.Count} email(s): {string.Join(", ", sent)}";
    }

    /// <summary>Groups cards into parts below the limit; a card too big on its own is reported.</summary>
    private static List<List<int>> Split(IReadOnlyList<MonthCard> cards, long limit, List<string> problems, string month)
    {
        const long overhead = 64 * 1024;
        var budget = (long)(limit * 0.9);
        var parts = new List<List<int>>();
        var current = new List<int>();
        long size = 0;
        foreach (var card in cards.OrderBy(c => c.CardId))
        {
            var cost = card.AttachmentBytes + overhead;
            if (cost > budget)
            {
                problems.Add($"A card created in {month} has {Mb(card.AttachmentBytes)} of attachments, too much for one email: it is only in the folder backup.");
                continue;
            }

            if (size + cost > budget && current.Count > 0)
            {
                parts.Add(current);
                current = [];
                size = 0;
            }

            current.Add(card.CardId);
            size += cost;
        }

        if (current.Count > 0)
        {
            parts.Add(current);
        }

        return parts;
    }

    private async Task SendAsync(BackupSettings s, string to, string subject, string body, string name, string path, CancellationToken cancellationToken) =>
        await mailer.SendAsync(s.EmailFrom, to, subject, body + "\n\nRestore: WorkTrace › Settings › Backup & restore › Restore, then choose this ZIP.\n",
            name, await File.ReadAllBytesAsync(path, cancellationToken), cancellationToken);

    private static string MonthBody(string month, (int Part, int Of)? part) =>
        $"Board cards created in {month} (UTC) with notes, comments, history and screenshots — the complete current state."
        + (part is { } p ? $" Part {p.Part} of {p.Of}; every part restores on its own." : "");

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024):0.0} MB";
}

/// <summary>Default secret store (non-Windows hosts and tests): memory only. The Windows app uses Credential Manager.</summary>
internal sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Read(string name) => _values.GetValueOrDefault(name);

    public void Write(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _values.Remove(name);
        }
        else
        {
            _values[name] = value;
        }
    }
}
