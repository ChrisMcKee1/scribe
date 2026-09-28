using System.Text.RegularExpressions;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// AsyncDeviceList: Settings reads the microphones on a worker, and until the list arrives the picker shows the current
/// choice alone. That entry must keep the choice (a save made before the list arrives writes it unchanged, never the
/// Windows default) and must claim nothing it does not know: not which device is the default, not that it is unavailable.
/// </summary>
public sealed class AsyncDeviceListTests
{
    [Fact]
    public void While_loading_the_Windows_default_is_shown_without_naming_a_device()
    {
        var menu = MicrophoneChoices.Loading(MicrophoneSelection.WindowsDefault);

        var choice = Assert.Single(menu.Choices);
        Assert.Equal(0, menu.SelectedIndex);
        Assert.Equal(MicrophoneChoiceKind.WindowsDefault, choice.Kind);
        Assert.Equal("Windows default", choice.Label);
        Assert.Equal(MicrophoneSelection.WindowsDefault, choice.Selection);
    }

    [Theory]
    [InlineData("{0.0.1.00000000}.{abc}", "Elgato Wave Neo", "Elgato Wave Neo")]
    [InlineData("{0.0.1.00000000}.{abc}", null, "Saved microphone")]
    [InlineData("{0.0.1.00000000}.{abc}", "   ", "Saved microphone")]
    public void While_loading_a_saved_microphone_stays_chosen_under_its_saved_name(string id, string? name, string label)
    {
        var saved = new MicrophoneSelection(id, name);

        var menu = MicrophoneChoices.Loading(saved);

        var choice = Assert.Single(menu.Choices);
        Assert.Equal(MicrophoneChoiceKind.Device, choice.Kind);
        Assert.Equal(label, choice.Label);
        Assert.Equal(saved, menu.Selected.Selection);
        Assert.DoesNotContain("Unavailable", choice.Label, StringComparison.Ordinal);
        Assert.DoesNotContain(MicrophoneChoices.DefaultSuffix, choice.Label, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_id_is_the_Windows_default_while_loading_too(string? id)
    {
        var menu = MicrophoneChoices.Loading(new MicrophoneSelection(id, "Old name"));

        Assert.Equal(MicrophoneChoiceKind.WindowsDefault, menu.Selected.Kind);
        Assert.Equal(MicrophoneSelection.WindowsDefault, menu.Selected.Selection);
    }

    [Fact]
    public void Once_the_list_arrives_the_choice_shown_while_loading_is_the_one_rebuilt_around()
    {
        // The window rebuilds around what the picker shows (ShownMicrophone), which while loading is this entry's selection.
        var saved = new MicrophoneSelection("mic-2", "USB mic");
        var shownWhileLoading = MicrophoneChoices.Loading(saved).Selected.Selection;
        Scribe.Core.Models.AudioDevice[] devices =
        [
            new("mic-1", "Laptop microphone", IsDefault: true),
            new("mic-2", "USB mic", IsDefault: false),
        ];

        var built = MicrophoneChoices.Build(devices, shownWhileLoading);

        Assert.Equal(MicrophoneChoiceKind.Device, built.Selected.Kind);
        Assert.Equal(saved, built.Selected.Selection);
    }

    [Fact]
    public void The_window_reads_off_its_thread_only_with_the_flag_on_and_drops_a_read_a_newer_list_overtook()
    {
        var window = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
        var populate = Slice(window, "private void PopulateDevices()", "public void ShowInputDevices(");
        Assert.Contains("if (_perfFlags.IsOn(PerfFlags.AsyncDeviceList))", populate, StringComparison.Ordinal);
        Assert.Contains("_inputDevices = _audio.GetInputDevices();", populate, StringComparison.Ordinal);
        Assert.Contains("ShowMicrophones(MicrophoneSelection.From(_settings));", populate, StringComparison.Ordinal);

        var load = Slice(window, "private async void LoadInputDevicesAsync()", "private MicrophoneSelection ShownMicrophone");
        Assert.Contains("devices = await Task.Run(_audio.GetInputDevices);", load, StringComparison.Ordinal);
        Assert.Contains("if (_closed || generation != _deviceListGeneration)", load, StringComparison.Ordinal);
        Assert.Contains("ShowInputDevices(devices);", load, StringComparison.Ordinal);

        var show = Slice(window, "public void ShowInputDevices(", "public void AdoptExternalMicrophone(");
        Assert.Contains("_deviceListGeneration++;", show, StringComparison.Ordinal);
        Assert.Contains("_microphonesLoading = false;", show, StringComparison.Ordinal);
        Assert.Contains("if (DeviceCombo.IsDropDownOpen)", show, StringComparison.Ordinal);

        Assert.Single(Regex.Matches(window, Regex.Escape("var menu = _microphonesLoading ? MicrophoneChoices.Loading(selection) : MicrophoneChoices.Build(_inputDevices, selection);")));
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker {start}.");
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing end marker {end}.");
        return source[startIndex..endIndex];
    }

    private static string RepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Scribe.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
