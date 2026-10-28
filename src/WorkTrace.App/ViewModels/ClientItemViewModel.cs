using WorkTrace.Core.Management;

namespace WorkTrace.App.ViewModels;

public sealed class ClientItemViewModel(ClientSummary summary, string lastUsedText)
{
    public ClientSummary Summary { get; } = summary;

    public int Id => Summary.Id;

    public string Name => Summary.Name;

    public bool IsInternal => Summary.IsInternal;

    public bool IsActive => Summary.IsActive;

    public string Details => Summary.ActiveTaskCount switch
    {
        0 => $"No active tasks · used {LastUsedText}",
        1 => $"1 task · used {LastUsedText}",
        var n => $"{n} tasks · used {LastUsedText}",
    };

    public string LastUsedText { get; } = lastUsedText;
}
