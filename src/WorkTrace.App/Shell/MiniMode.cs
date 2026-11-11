namespace WorkTrace.App.Shell;

/// <summary>
/// Switches between the normal window and Mini Mode (the Today panel in a small window, always on top by default).
/// Only windows change: the same application, database, view models and tracking engine keep running.
/// </summary>
public interface IMiniMode
{
    bool IsActive { get; }

    /// <summary>Enters Mini Mode, or returns to the normal window. Call on the UI thread.</summary>
    void Toggle();
}
