namespace Scribe.Core.Settings;

public enum DurationChoiceKind
{
    HistoryRetention,
    MaxDictation,
    IdleRelease,
}

public sealed record DurationChoice(int? Value, string Label, bool IsCustom, bool IsSelected);

public sealed record DurationChoiceSet(
    DurationChoiceKind Kind,
    int Minimum,
    int Maximum,
    string Unit,
    IReadOnlyList<DurationChoice> Choices)
{
    public bool IsOutOfRange(int value) => value < Minimum || value > Maximum;
}

public static class DurationChoices
{
    public const string CustomLabel = "Custom...";

    public static DurationChoiceSet Build(DurationChoiceKind kind, int storedValue)
    {
        var spec = Spec.For(kind);
        var presets = spec.Presets
            .Select(preset => new DurationChoice(preset.Value, preset.Label, IsCustom: false, preset.Value == storedValue))
            .ToList();
        var customSelected = !presets.Any(choice => choice.IsSelected);
        presets.Add(new DurationChoice(null, CustomLabel, IsCustom: true, customSelected));
        return new DurationChoiceSet(kind, spec.Minimum, spec.Maximum, spec.Unit, presets);
    }

    public static ValidationIssue? ValidateCustom(
        DurationChoiceKind kind,
        SettingsPage page,
        string controlName,
        int value)
    {
        var spec = Spec.For(kind);
        return value < spec.Minimum || value > spec.Maximum
            ? new ValidationIssue(
                ValidationCode.DurationOutOfRange,
                page,
                controlName,
                RowKey: null,
                SettingsDraftValidator.DurationOutOfRangeMessage(spec.Minimum, spec.Maximum))
            : null;
    }

    private sealed record Preset(int Value, string Label);

    private sealed record Spec(int Minimum, int Maximum, string Unit, IReadOnlyList<Preset> Presets)
    {
        public static Spec For(DurationChoiceKind kind) => kind switch
        {
            DurationChoiceKind.HistoryRetention => new(
                1,
                3650,
                "days",
                [
                    new(7, "For 7 days"),
                    new(30, "For 30 days"),
                    new(90, "For 90 days (default)"),
                    new(365, "For 1 year"),
                    new(0, "Until I delete them"),
                ]),
            DurationChoiceKind.MaxDictation => new(
                1,
                1440,
                "minutes",
                [
                    new(5, "5 minutes"),
                    new(10, "10 minutes (default)"),
                    new(30, "30 minutes"),
                    new(60, "1 hour"),
                    new(0, "No limit"),
                ]),
            DurationChoiceKind.IdleRelease => new(
                1,
                120,
                "minutes",
                [
                    new(0, "Never"),
                    new(5, "After 5 minutes"),
                    new(10, "After 10 minutes (default)"),
                    new(30, "After 30 minutes"),
                    new(60, "After 1 hour"),
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }
}

public sealed record ThreadChoice(int Value, string Label, bool IsSelected);

public static class ThreadChoices
{
    public const int Minimum = 1;
    public const int Maximum = 16;

    public static IReadOnlyList<ThreadChoice> Build(int storedValue)
    {
        var choices = new List<ThreadChoice>
        {
            new(0, "Automatic (recommended)", storedValue == 0),
        };
        choices.AddRange(Enumerable.Range(Minimum, Maximum)
            .Select(value => new ThreadChoice(value, value.ToString(), value == storedValue)));
        return choices;
    }

    public static bool IsOutOfRange(int value) => value != 0 && (value < Minimum || value > Maximum);
}
