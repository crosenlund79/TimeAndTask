namespace WorkTrace.App.Shell;

public sealed record NavigationRequest(AppSection Section, object? Parameter);

/// <summary>Decouples section view models from the main window that hosts them.</summary>
public interface INavigationService
{
    event EventHandler<NavigationRequest>? NavigationRequested;

    void Navigate(AppSection section, object? parameter = null);
}

internal sealed class NavigationService : INavigationService
{
    public event EventHandler<NavigationRequest>? NavigationRequested;

    public void Navigate(AppSection section, object? parameter = null) =>
        NavigationRequested?.Invoke(this, new NavigationRequest(section, parameter));
}

/// <summary>Navigation parameter: show the tasks of one client.</summary>
public sealed record ClientTasksFilter(int ClientId);

/// <summary>Navigation parameter: start creating a new item in the target section.</summary>
public sealed record CreateNewRequest;

/// <summary>Navigation parameter (Tasks): show the Inbox with the cursor in the quick-capture box.</summary>
public sealed record CaptureRequest;
