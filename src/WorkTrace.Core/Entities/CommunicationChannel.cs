namespace WorkTrace.Core.Entities;

/// <summary>How a piece of work reached the user (phone, Teams, email, ...).</summary>
public class CommunicationChannel : LookupItem
{
    public static readonly IReadOnlyList<string> BuiltInNames =
        ["Phone", "Microsoft Teams", "Email", "Verbal", "Other", "Unspecified"];
}
