namespace WorkTrace.App.ViewModels;

/// <summary>A page in the main window.</summary>
public interface ISectionViewModel
{
    /// <summary>Called whenever the section becomes visible (reload data, apply navigation parameter).</summary>
    Task ActivateAsync(object? parameter);

    /// <summary>True when the section holds unsaved edits.</summary>
    bool HasUnsavedChanges { get; }
}
