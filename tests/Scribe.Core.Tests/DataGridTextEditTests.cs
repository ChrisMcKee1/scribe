using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Scribe.App.Settings;

namespace Scribe.Core.Tests;

public sealed class DataGridTextEditTests
{
    [Fact]
    public void A_virtualized_row_gets_keyboard_focus_in_its_actual_text_editor() => OnPrivateDesktop(() =>
    {
        using var rig = new GridWindow(200);

        Assert.True(DataGridTextEdit.Begin(rig.Grid, rig.Rows[199], rig.Written, selectAll: true));

        var editor = Assert.IsType<TextBox>(rig.Written.GetCellContent(rig.Rows[199]));
        Assert.Same(editor, Keyboard.FocusedElement);
        Assert.True(editor.IsKeyboardFocused);
        Assert.Equal("Written 199", editor.SelectedText);
        Assert.Same(rig.Rows[199], rig.Grid.CurrentCell.Item);
    });

    [Fact]
    public void Tab_commits_the_spoken_form_and_places_the_caret_in_the_written_form() => OnPrivateDesktop(() =>
    {
        using var rig = new GridWindow(2);
        DataGridTypingTab.Attach(rig.Grid, () => Assert.Fail("Tab could not focus the next editor."));
        Assert.True(DataGridTextEdit.Begin(rig.Grid, rig.Rows[0], rig.Spoken, selectAll: false));
        var spoken = Assert.IsType<TextBox>(rig.Spoken.GetCellContent(rig.Rows[0]));
        spoken.SetCurrentValue(TextBox.TextProperty, "edited phrase");

        var tab = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(spoken), Environment.TickCount, Key.Tab)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        spoken.RaiseEvent(tab);

        Assert.True(tab.Handled);
        Assert.Equal("edited phrase", rig.Rows[0].Pattern);
        var written = Assert.IsType<TextBox>(rig.Written.GetCellContent(rig.Rows[0]));
        Assert.Same(written, Keyboard.FocusedElement);
        Assert.True(written.IsKeyboardFocused);
    });

    [Fact]
    public void A_first_click_on_a_text_cell_starts_editing_without_a_second_click() => OnPrivateDesktop(() =>
    {
        using var rig = new GridWindow(2);
        DataGridTextEdit.Attach(rig.Grid, () => Assert.Fail("A text click could not focus its editor."));
        rig.After.Focus();
        var text = Assert.IsType<TextBlock>(rig.Spoken.GetCellContent(rig.Rows[1]));
        text.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseDownEvent,
        });

        var editor = Assert.IsType<TextBox>(rig.Spoken.GetCellContent(rig.Rows[1]));
        Assert.Same(editor, Keyboard.FocusedElement);
        Assert.True(editor.IsKeyboardFocused);
        Assert.Same(rig.Rows[1], rig.Grid.CurrentCell.Item);
    });

    [Fact]
    public void Returning_to_an_edited_row_focuses_the_requested_field_not_the_last_cell() => OnPrivateDesktop(() =>
    {
        using var rig = new GridWindow(2);
        Assert.True(DataGridTextEdit.Begin(rig.Grid, rig.Rows[0], rig.Spoken, selectAll: false));
        Assert.IsType<TextBox>(rig.Spoken.GetCellContent(rig.Rows[0])).SetCurrentValue(TextBox.TextProperty, "changed");
        Assert.True(rig.Grid.CommitEdit(DataGridEditingUnit.Row, true));
        Assert.True(DataGridTextEdit.Begin(rig.Grid, rig.Rows[1], rig.Written, selectAll: false));
        Assert.True(rig.Grid.CommitEdit(DataGridEditingUnit.Row, true));

        Assert.True(DataGridTextEdit.Begin(rig.Grid, rig.Rows[0], rig.Spoken, selectAll: true));

        var editor = Assert.IsType<TextBox>(rig.Spoken.GetCellContent(rig.Rows[0]));
        Assert.Same(editor, Keyboard.FocusedElement);
        Assert.Equal("changed", editor.SelectedText);
    });

    private static void OnPrivateDesktop(Action action, [CallerMemberName] string testName = "")
    {
        PrivateDesktopTest.Run(typeof(DataGridTextEditTests), action, testName);
    }

    private sealed class GridWindow : IDisposable
    {
        private readonly Window _window;
        public DataGrid Grid { get; }
        public DataGridTextColumn Spoken { get; }
        public DataGridTextColumn Written { get; }
        public Button After { get; } = new() { Content = "After grid" };
        public TestRow[] Rows { get; }

        public GridWindow(int rowCount)
        {
            Rows = Enumerable.Range(0, rowCount)
                .Select(index => new TestRow { Pattern = $"Spoken {index}", Replacement = $"Written {index}" }).ToArray();
            Grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                ItemsSource = Rows,
                Height = 160,
            };
            KeyboardNavigation.SetTabNavigation(Grid, KeyboardNavigationMode.Once);
            Spoken = new DataGridTextColumn
            {
                Header = "Spoken",
                Binding = new Binding(nameof(TestRow.Pattern)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            };
            Written = new DataGridTextColumn
            {
                Header = "Written",
                Binding = new Binding(nameof(TestRow.Replacement)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            };
            Grid.Columns.Add(Spoken);
            Grid.Columns.Add(Written);
            var panel = new StackPanel();
            panel.Children.Add(Grid);
            panel.Children.Add(After);
            _window = new Window { Width = 500, Height = 300, Content = panel, ShowInTaskbar = false };
            _window.Show();
            _window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }

        public void Dispose() => _window.Close();
    }

    public sealed class TestRow
    {
        public string Pattern { get; set; } = string.Empty;
        public string Replacement { get; set; } = string.Empty;
    }

}
