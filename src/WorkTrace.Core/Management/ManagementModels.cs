namespace WorkTrace.Core.Management;

// ---- Inputs (what the user edits) ----

public sealed record ClientInput(string? Name, string? Description, bool IsInternal, string? Color = null);

public sealed record TaskInput(
    int? ClientId,
    int? TaskSystemId,
    string? ExternalId,
    string? Title,
    string? Notes,
    int? DefaultChannelId,
    bool? ShowOnBoard = null,
    DateOnly? Deadline = null);

// ---- Read models (what lists and pickers show) ----

public sealed record ClientSummary(
    int Id,
    string Name,
    string? Description,
    bool IsInternal,
    bool IsActive,
    int TaskCount,
    int ActiveTaskCount,
    DateTime? LastUsedAtUtc,
    DateTime ModifiedAtUtc,
    string Color = "#7A7F87");

public sealed record TaskSummary(
    int Id,
    int ClientId,
    string ClientName,
    bool ClientIsActive,
    int? TaskSystemId,
    string? TaskSystemName,
    string? ExternalId,
    string Title,
    string? Notes,
    int? DefaultChannelId,
    string? DefaultChannelName,
    bool IsArchived,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    DateTime? LastUsedAtUtc,
    bool IsOnBoard = false,
    DateOnly? Deadline = null,
    string ClientColor = "#7A7F87",
    DeadlineState DeadlineState = DeadlineState.None);

public enum LookupKind
{
    TaskSystem = 0,
    CommunicationChannel = 1,
}

public sealed record LookupSummary(
    LookupKind Kind,
    int Id,
    string Name,
    int SortOrder,
    bool IsActive,
    bool IsBuiltIn,
    int UsageCount);

// ---- Queries ----

public sealed record TaskQuery(
    string? Text = null,
    int? ClientId = null,
    bool IncludeArchived = false,
    bool IncludeInactiveClients = false,
    int Limit = 200);
