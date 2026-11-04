using System.Windows.Controls;
using System.Windows.Input;

namespace WorkTrace.App.Views;

public partial class GuidesView : UserControl
{
    public GuidesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ViewModels.GuidesViewModel old)
            {
                old.EditorFocusRequested -= OnEditorFocusRequested;
            }

            if (e.NewValue is ViewModels.GuidesViewModel vm)
            {
                vm.EditorFocusRequested += OnEditorFocusRequested;
            }
        };
    }

    private void OnEditorFocusRequested(object? sender, EventArgs e) => ListNavigation.FocusSoon(GuideTitleBox);

    /// <summary>Double-click: open the only document, offer a choice when there are several, else show the guide.</summary>
    private async void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ViewModels.GuidesViewModel vm
            || ItemsControl.ContainerFromElement(GuideList, (System.Windows.DependencyObject)e.OriginalSource) is not ListBoxItem { DataContext: ViewModels.GuideItemViewModel item })
        {
            return;
        }

        e.Handled = true;
        var documents = await vm.GetDocumentsAsync(item);
        if (documents.Count == 0)
        {
            vm.EditSelectedCommand.Execute(null);
        }
        else if (documents.Count == 1)
        {
            vm.OpenDocument(documents[0]);
        }
        else
        {
            var menu = new ContextMenu { PlacementTarget = GuideList, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            foreach (var document in documents)
            {
                var entry = new MenuItem { Header = document.FileName };
                entry.Click += (_, _) => vm.OpenDocument(document);
                menu.Items.Add(entry);
            }

            menu.IsOpen = true;
        }
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ViewModels.GuidesViewModel vm)
        {
            vm.SetSelection(GuideList.SelectedItems.OfType<ViewModels.GuideItemViewModel>());
        }
    }

    private void OnSelectAll(object sender, System.Windows.RoutedEventArgs e)
    {
        GuideList.SelectAll();
        GuideList.Focus();
    }

    private void OnFind(object sender, ExecutedRoutedEventArgs e) => ListNavigation.FocusSoon(SearchBox, selectAll: true);
}
