using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WorkTrace.App.ViewModels;
using WorkTrace.App.Views;
using WorkTrace.Core.Reporting;

namespace WorkTrace.App.Shell;

/// <summary>Opens the registration editor (edit a registration, or classify unclassified time).</summary>
public interface IRegistrationEditor
{
    /// <summary>True when something was saved.</summary>
    Task<bool> EditAsync(RegistrationEntry entry);
}

internal sealed class RegistrationEditor(IServiceProvider services) : IRegistrationEditor
{
    public async Task<bool> EditAsync(RegistrationEntry entry)
    {
        var viewModel = ActivatorUtilities.CreateInstance<RegistrationEditorViewModel>(services);
        await viewModel.LoadAsync(entry);
        var window = new RegistrationEditorWindow(viewModel);
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        return window.ShowDialog() == true;
    }
}
