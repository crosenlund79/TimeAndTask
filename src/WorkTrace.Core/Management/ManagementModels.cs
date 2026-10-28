namespace WorkTrace.Core.Management;

// ---- Inputs (what the user edits) ----

public sealed record ClientInput(string? Name, string? Description, bool IsInternal);

public sealed record TaskInput(
    int? ClientId,
    int? TaskSystemId,
    string? ExternalId,
    string? Title,
    string? Notes,
    int? DefaultChannelId);

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
    DateTime ModifiedAtUtc);

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
    DateTime? LastUsedAtUtc);

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
