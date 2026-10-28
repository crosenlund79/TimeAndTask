using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorkTrace.Core.Common;

namespace WorkTrace.Infrastructure.Persistence;

/// <summary>
/// Enforces UTC on write and restores <see cref="DateTimeKind.Utc"/> on read. SQLite has no
/// timestamp type; values are stored as ISO-8601 text, which sorts chronologically.
/// </summary>
internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => UtcTime.Ensure(v, null),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
