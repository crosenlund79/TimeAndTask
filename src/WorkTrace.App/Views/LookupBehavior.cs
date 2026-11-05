using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.Views;

/// <summary>
/// One keyboard behaviour for every lookup (ComboBox) in WorkTrace (window behaviour only):
/// Alt+↓ (or F4) opens, ↑/↓ move, Enter selects, Esc closes without changing the selection, and in editable
/// lookups typing filters the list (case- and accent-insensitive, matching anywhere in the text).
/// Registered once at start-up for all ComboBoxes.
/// </summary>
internal static class LookupBehavior
{
    private static readonly DependencyProperty AttachedProperty =
        DependencyProperty.RegisterAttached("LookupAttached", typeof(bool), typeof(LookupBehavior));

    /// <summary>Set by keystrokes in the editable text; only then does a text change filter (and open) the list.</summary>
    private static readonly DependencyProperty UserTypingProperty =
        DependencyProperty.RegisterAttached("LookupUserTyping", typeof(bool), typeof(LookupBehavior));

    private static readonly DependencyProperty OpenedWithProperty =
        DependencyProperty.RegisterAttached("LookupOpenedWith", typeof(object), typeof(LookupBehavior));

    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox box || (bool)box.GetValue(AttachedProperty))
        {
            return;
        }

        box.SetValue(AttachedProperty, true);
        box.DropDownOpened += (_, _) => box.SetValue(OpenedWithProperty, new Selection(box.SelectedItem));
        box.DropDownClosed += (_, _) =>
        {
            box.ClearValue(OpenedWithProperty);

            // Never change the list while the popup is closing (WPF: "Cannot reopen a popup in the closed event
            // handler"); clear the filter once the close has completed.
            box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!box.IsDropDownOpen && box.Items.Filter is not null)
                {
                    box.Items.Filter = null;
                }
            });
        };

        if (box.IsEditable && box.Template?.FindName("PART_EditableTextBox", box) is TextBox editor)
        {
            editor.PreviewTextInput += (_, _) => box.SetValue(UserTypingProperty, true);
            editor.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is Key.Back or Key.Delete || (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
                {
                    box.SetValue(UserTypingProperty, true);
                }
            };
            editor.TextChanged += (_, _) => Filter(box, editor);
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ComboBox box)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt && !box.IsDropDownOpen)
        {
            box.IsDropDownOpen = true;
            e.Handled = true;
        }
        else if (key == Key.Escape && box.IsDropDownOpen)
        {
            // Close without selecting: put back what was selected when the list opened.
            if (box.GetValue(OpenedWithProperty) is Selection opened && !Equals(box.SelectedItem, opened.Item))
            {
                box.Items.Filter = null;
                box.SelectedItem = opened.Item;
            }

            box.IsDropDownOpen = false;
            e.Handled = true;
        }
        else if (key == Key.Enter && box.IsDropDownOpen)
        {
            box.IsDropDownOpen = false; // keeps the highlighted entry
            e.Handled = true;
        }
    }

    /// <summary>Editable lookups: show only entries containing the typed text (only while the user types).</summary>
    private static void Filter(ComboBox box, TextBox editor)
    {
        // Text set by a selection, a binding or a reload is not typing: it never filters or opens the list.
        var typing = (bool)box.GetValue(UserTypingProperty);
        box.SetValue(UserTypingProperty, false);
        if (!typing || !editor.IsKeyboardFocusWithin)
        {
            return;
        }

        var text = editor.Text;
        var selected = box.SelectedItem is { } item ? DisplayText(box, item) : null;
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, selected, StringComparison.Ordinal))
        {
            box.Items.Filter = null;
            return;
        }

        var caret = editor.CaretIndex;
        box.Items.Filter = o => Text.IndexOf(DisplayText(box, o), text.Trim()) >= 0;
        if (!box.IsDropDownOpen && !box.Items.IsEmpty)
        {
            // Opened after the current input is processed (never from inside another popup event).
            box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!box.IsDropDownOpen && editor.IsKeyboardFocusWithin && !box.Items.IsEmpty)
                {
                    box.IsDropDownOpen = true;

                    // Opening the list selects the text; keep typing where the user was.
                    editor.SelectionLength = 0;
                    editor.CaretIndex = Math.Min(caret, editor.Text.Length);
                }
            });
        }
    }

    private static string DisplayText(ComboBox box, object item)
    {
        var path = TextSearch.GetTextPath(box) is { Length: > 0 } p ? p : box.DisplayMemberPath;
        if (!string.IsNullOrEmpty(path) && item.GetType().GetProperty(path, BindingFlags.Public | BindingFlags.Instance) is { } property)
        {
            return property.GetValue(item)?.ToString() ?? "";
        }

        return item.ToString() ?? "";
    }

    /// <summary>Boxed selection, so "nothing selected" can be told apart from "not recorded".</summary>
    private sealed record Selection(object? Item);
}
