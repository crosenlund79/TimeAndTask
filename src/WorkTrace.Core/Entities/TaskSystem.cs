namespace WorkTrace.Core.Entities;

/// <summary>The system a task originates from (D365FO, CRM, Azure DevOps, ...).</summary>
public class TaskSystem : LookupItem
{
    public static readonly IReadOnlyList<string> BuiltInNames =
        ["D365FO", "CRM", "Azure DevOps", "Other", "None / Unspecified"];
}
