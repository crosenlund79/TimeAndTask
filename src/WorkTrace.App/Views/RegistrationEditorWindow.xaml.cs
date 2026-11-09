using System.Windows;
using WorkTrace.App.ViewModels;

namespace WorkTrace.App.Views;

/// <summary>Modal editor for one registration (window behaviour only; the view model does the work).</summary>
public partial class RegistrationEditorWindow : Window
{
    public RegistrationEditorWindow(RegistrationEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, saved) =>
        {
            DialogResult = saved;
        };
    }
}
