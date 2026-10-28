using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.ViewModels;

/// <summary>Shared error handling for view models that call services.</summary>
public abstract partial class ViewModelBase(INotificationService notifications, ILogger logger) : ObservableObject
{
    protected INotificationService Notifications { get; } = notifications;

    protected ILogger Logger { get; } = logger;

    [ObservableProperty]
    public partial bool IsBusy { get; protected set; }

    /// <summary>
    /// Runs a service operation and converts expected failures into notifications. Returns false when
    /// the operation failed. Validation errors are rethrown for editors to display per field.
    /// </summary>
    protected async Task<bool> RunAsync(Func<Task> operation, string failureMessage, Func<Task>? onNotFound = null)
    {
        try
        {
            IsBusy = true;
            await operation();
            return true;
        }
        catch (DomainValidationException)
        {
            throw;
        }
        catch (OperationNotAllowedException ex)
        {
            Notifications.Show(ex.Message, NotificationKind.Warning);
        }
        catch (EntityNotFoundException ex)
        {
            Notifications.Show(ex.Message, NotificationKind.Warning);
            if (onNotFound is not null)
            {
                await onNotFound();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Operation} failed", failureMessage);
            Notifications.Show($"{failureMessage} See the log for details.", NotificationKind.Error);
        }
        finally
        {
            IsBusy = false;
        }

        return false;
    }
}
