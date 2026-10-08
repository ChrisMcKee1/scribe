using System.Windows.Controls;
using Scribe.Core.Models;
using Scribe.Core.TextInjection;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private sealed record TextFormatChoice(DictationTextFormat? Format, string Label);
    private sealed record ProfileInjectionChoice(InjectionMethod? Method, string Label);
    private sealed record ProfileShiftEnterChoice(bool? ShiftEnter, string Label);

    private DictationTextFormat SelectedDefaultTextFormat =>
        (DefaultTextFormatCombo.SelectedItem as TextFormatChoice)?.Format ?? DictationTextFormat.Plain;

    private void PopulateFormattingChoices()
    {
        DefaultTextFormatCombo.DisplayMemberPath = nameof(TextFormatChoice.Label);
        DefaultTextFormatCombo.ItemsSource = new[]
        {
            new TextFormatChoice(DictationTextFormat.Plain, "Plain text"),
            new TextFormatChoice(DictationTextFormat.MarkdownSource, "Literal Markdown source"),
        };
        FormattingSourceHint.Text = DictationFormattingText.SourceLimits;
    }

    private void LoadFormattingControls(AppSettings source)
    {
        AppAwareFormattingCheck.IsChecked = source.AppAwareFormattingEnabled;
        var formats = (TextFormatChoice[])DefaultTextFormatCombo.ItemsSource;
        DefaultTextFormatCombo.SelectedItem =
            formats.FirstOrDefault(choice => choice.Format == source.DefaultTextFormat) ?? formats[0];
    }

    private void PopulateProfileFormattingChoices()
    {
        ProfileTextFormatCombo.DisplayMemberPath = nameof(TextFormatChoice.Label);
        ProfileTextFormatCombo.ItemsSource = new[]
        {
            new TextFormatChoice(null, "Use the default text format"),
            new TextFormatChoice(DictationTextFormat.Plain, "Plain text"),
            new TextFormatChoice(DictationTextFormat.MarkdownSource, "Literal Markdown source"),
        };
        ProfileInjectionCombo.DisplayMemberPath = nameof(ProfileInjectionChoice.Label);
        ProfileInjectionCombo.ItemsSource = new[]
        {
            new ProfileInjectionChoice(null, "Use the Advanced setting"),
            new ProfileInjectionChoice(InjectionMethod.UnicodeType, "Type the text"),
            new ProfileInjectionChoice(InjectionMethod.ClipboardPaste, "Paste plain text"),
        };
        ProfileShiftEnterCombo.DisplayMemberPath = nameof(ProfileShiftEnterChoice.Label);
        ProfileShiftEnterCombo.ItemsSource = new[]
        {
            new ProfileShiftEnterChoice(null, "Use the Advanced setting"),
            new ProfileShiftEnterChoice(true, "Shift+Enter"),
            new ProfileShiftEnterChoice(false, "Enter"),
        };
    }

    private void ShowProfileFormatting(ProfileRow row)
    {
        var formats = (TextFormatChoice[])ProfileTextFormatCombo.ItemsSource;
        ProfileTextFormatCombo.SelectedItem =
            formats.FirstOrDefault(choice => choice.Format == row.TextFormat) ?? formats[0];
        var methods = (ProfileInjectionChoice[])ProfileInjectionCombo.ItemsSource;
        ProfileInjectionCombo.SelectedItem =
            methods.FirstOrDefault(choice => choice.Method == row.InjectionMethod) ?? methods[0];
        var lineBreaks = (ProfileShiftEnterChoice[])ProfileShiftEnterCombo.ItemsSource;
        ProfileShiftEnterCombo.SelectedItem =
            lineBreaks.FirstOrDefault(choice => choice.ShiftEnter == row.ShiftEnterLineBreaks) ?? lineBreaks[0];
    }

    private void ProfileFormatting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingProfile && SelectedProfile is { } row)
        {
            row.TextFormat = (ProfileTextFormatCombo.SelectedItem as TextFormatChoice)?.Format;
            row.InjectionMethod = (ProfileInjectionCombo.SelectedItem as ProfileInjectionChoice)?.Method;
            row.ShiftEnterLineBreaks = (ProfileShiftEnterCombo.SelectedItem as ProfileShiftEnterChoice)?.ShiftEnter;
            row.Touched = true;
        }
    }
}
