using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Scribe.Core.Settings;
using Row = Scribe.Core.Settings.DictionaryEntryBuilder.Row;

namespace Scribe.Core.Tests;

public sealed class DictionaryWordWindowTests
{
    [Fact]
    public void Real_dialog_adds_removes_and_focuses_literal_ways_at_normal_and_large_text_sizes() =>
        PrivateDesktopTest.Run(typeof(DictionaryWordWindowTests), () =>
        {
            using var rig = new DialogRig();
            foreach (var scale in new[] { 1.0, 2.25 })
            {
                rig.SetTextScale(scale);
                var window = rig.Open(null, (written, forms) => DictionaryWordEditor.Build([], null, written, forms));
                var written = Get<TextBox>(window, "WrittenBox");
                Assert.Same(written, Keyboard.FocusedElement);
                written.Text = "Contoso, Ltd.";
                var panel = Get<StackPanel>(window, "FormsPanel");
                FormBox(panel, 0).Text = "contoso limited";
                Assert.True(written.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(FormBox(panel, 0), Keyboard.FocusedElement);

                Click(Get<Button>(window, "AddWayButton"));
                Assert.Same(FormBox(panel, 1), Keyboard.FocusedElement);
                FormBox(panel, 1).Text = "discarded";
                Click(Get<Button>(window, "AddWayButton"));
                var last = FormBox(panel, 2);
                last.Text = "contoso, ltd";
                Click(Assert.Single(((Grid)panel.Children[1]).Children.OfType<Button>()));
                Assert.Same(last, Keyboard.FocusedElement);
                Assert.Equal(2, panel.Children.Count);
                Assert.Equal("Scribe hears, way 2 of 2", new TextBoxAutomationPeer(last).GetName());

                var accept = Get<Button>(window, "AcceptButton");
                var position = accept.TranslatePoint(new Point(), (FrameworkElement)window.Content);
                Assert.InRange(position.Y + accept.ActualHeight, 0, window.ActualHeight);
                Click(accept);
                var result = rig.Choice(window);
                Assert.True(result.Succeeded);
                Assert.Equal(new[] { "contoso limited", "contoso, ltd" }, result.AddedRows.Select(row => row.Pattern));
                Assert.All(result.AddedRows, row => Assert.Equal("Contoso, Ltd.", row.Replacement));
            }
        });

    [Fact]
    public void Duplicate_validation_keeps_the_dialog_open_and_focuses_the_conflicting_box() =>
        PrivateDesktopTest.Run(typeof(DictionaryWordWindowTests), () =>
        {
            using var rig = new DialogRig();
            var window = rig.Open(null, (written, forms) => DictionaryWordEditor.Build([], null, written, forms));
            var panel = Get<StackPanel>(window, "FormsPanel");
            FormBox(panel, 0).Text = "same";
            Click(Get<Button>(window, "AddWayButton"));
            FormBox(panel, 1).Text = " SAME ";

            Click(Get<Button>(window, "AcceptButton"));

            Assert.True(window.IsVisible);
            Assert.Same(FormBox(panel, 1), Keyboard.FocusedElement);
            Assert.Equal(Visibility.Visible, Get<TextBlock>(window, "ErrorText").Visibility);
            FormBox(panel, 1).Text = "different";
            Click(Get<Button>(window, "AcceptButton"));
            Assert.Equal(2, rig.Choice(window).AddedRows.Count);
        });

    [Fact]
    public void Done_round_trips_untouched_line_endings_spaces_and_rule_flags() =>
        PrivateDesktopTest.Run(typeof(DictionaryWordWindowTests), () =>
        {
            using var rig = new DialogRig();
            var original = new Row(7, " two  words,\nnext ", "Template\nbody\r\nend ", false, false);
            var window = rig.Open(original,
                (written, forms) => DictionaryWordEditor.Build([original], 0, written, forms));

            Click(Get<Button>(window, "AcceptButton"));

            Assert.Equal(original, rig.Choice(window).EditedRow);
        });

    [Fact]
    public void Escape_discards_dialog_text_without_submitting_or_closing_the_owner() =>
        PrivateDesktopTest.Run(typeof(DictionaryWordWindowTests), () =>
        {
            using var rig = new DialogRig();
            var builds = 0;
            var ownerEscapes = 0;
            rig.Owner.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) ownerEscapes++; };
            var window = rig.Open(null, (written, forms) =>
            {
                builds++;
                return DictionaryWordEditor.Build([], null, written, forms);
            });

            var written = Get<TextBox>(window, "WrittenBox");
            written.Text = "Unsaved";
            written.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(written), 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });

            Assert.False(window.IsVisible);
            Assert.True(rig.Owner.IsVisible);
            Assert.Equal(0, builds);
            Assert.Equal(0, ownerEscapes);
        });

    [Fact]
    public void An_abandoned_composition_gets_one_escape_without_permanently_disabling_dialog_escape() =>
        PrivateDesktopTest.Run(typeof(DictionaryWordWindowTests), () =>
        {
            using var rig = new DialogRig();
            var window = rig.Open(null, (written, forms) => DictionaryWordEditor.Build([], null, written, forms));
            var written = Get<TextBox>(window, "WrittenBox");
            written.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                new TextComposition(InputManager.Current, written, string.Empty))
            {
                RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent,
            });
            Escape(written);
            Assert.True(window.IsVisible);
            Escape(written);
            Assert.False(window.IsVisible);
            Assert.True(rig.Owner.IsVisible);
        });

    private static T Get<T>(Window window, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static TextBox FormBox(StackPanel panel, int index) =>
        Assert.Single(((Grid)panel.Children[index]).Children.OfType<TextBox>());

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static void Escape(TextBox box) =>
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });

    private sealed class DialogRig : IDisposable
    {
        private readonly Application _app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        private readonly Assembly _assembly;
        private readonly Type _type;
        private readonly string _appBin;
        public Window Owner { get; } = new() { Width = 1040, Height = 860, ShowInTaskbar = false };

        public DialogRig()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
            {
                root = root.Parent;
            }

            Assert.NotNull(root);
            var output = new DirectoryInfo(AppContext.BaseDirectory);
            while (output is not null && output.Name is not ("Debug" or "Release"))
            {
                output = output.Parent;
            }

            Assert.NotNull(output);
            _appBin = Path.Combine(root.FullName, "src", "Scribe.App", "bin", output.Name, "net10.0-windows10.0.22000.0");
            AssemblyLoadContext.Default.Resolving += Resolve;
            _assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(_appBin, "Scribe.dll"));
            _type = _assembly.GetType("Scribe.App.Settings.DictionaryWordWindow", throwOnError: true)!;
            var document = XDocument.Load(Path.Combine(root.FullName, "src", "Scribe.App", "App.xaml"));
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resources = document.Descendants(xaml + "ResourceDictionary").First();
            foreach (var attribute in document.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                var value = attribute.Value.StartsWith("clr-namespace:", StringComparison.Ordinal) &&
                    !attribute.Value.Contains(";assembly=", StringComparison.Ordinal)
                    ? attribute.Value + ";assembly=Scribe"
                    : attribute.Value;
                resources.SetAttributeValue(attribute.Name, value);
            }

            // Load only the resources, never Scribe.App.App or its posted startup callback.
            _app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
            Owner.Show();
            Owner.UpdateLayout();
        }

        public void SetTextScale(double factor)
        {
            foreach (var (key, size) in new[]
            {
                ("ScribeFontCaption", 12), ("ScribeFontBody", 14),
                ("ScribeFontBodyLarge", 16), ("ScribeFontSubtitle", 20), ("ScribeFontTitle", 26),
                ("ControlContentThemeFontSize", 14), ("TextControlThemeFontSize", 14), ("TitleBarThemeFontSize", 12),
            })
            {
                _app.Resources[key] = size * factor;
            }

            _assembly.GetType("Scribe.App.Infrastructure.TextScaleService", true)!
                .GetProperty("CurrentFactor")!.GetSetMethod(nonPublic: true)!.Invoke(null, [factor]);
        }

        public Window Open(Row? original, Func<string, IReadOnlyList<string>, DictionaryWordEditor.Result> build)
        {
            var window = (Window)Activator.CreateInstance(_type, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Owner, original, build], culture: null)!;
            window.Show();
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            return window;
        }

        public DictionaryWordEditor.Result Choice(Window window) =>
            Assert.IsType<DictionaryWordEditor.Result>(_type.GetField("_choice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));

        private Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var path = Path.Combine(_appBin, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }

        public void Dispose()
        {
            foreach (Window window in _app.Windows.Cast<Window>().ToArray())
            {
                window.Close();
            }

            _app.Shutdown();
            AssemblyLoadContext.Default.Resolving -= Resolve;
        }
    }
}
