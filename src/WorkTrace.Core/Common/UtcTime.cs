namespace WorkTrace.Core.Common;

/// <summary>
/// Guards for the "all persisted timestamps are UTC" invariant.
/// </summary>
public static class UtcTime
{
    /// <summary>
    /// Returns <paramref name="value"/> as a UTC <see cref="DateTime"/>.
    /// Local values are converted; <see cref="DateTimeKind.Unspecified"/> is rejected because
    /// its meaning is ambiguous and would silently corrupt timelines.
    /// </summary>
    public static DateTime Ensure(DateTime value, string? paramName = null) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => throw new ArgumentException(
            "Timestamp must have DateTimeKind.Utc or DateTimeKind.Local, not Unspecified.",
            paramName ?? nameof(value)),
    };

    public static DateTime? Ensure(DateTime? value, string? paramName = null) =>
        value is null ? null : Ensure(value.Value, paramName);

    /// <summary>Current UTC time from the given provider as a <see cref="DateTime"/> with Kind=Utc.</summary>
    public static DateTime Now(TimeProvider timeProvider) => timeProvider.GetUtcNow().UtcDateTime;
}
