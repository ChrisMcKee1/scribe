using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Scribe.Core.Settings;
using Windows.UI.ViewManagement;

namespace Scribe.App.Infrastructure;

public sealed class TextScaleService : IDisposable
{
    public const string OverrideEnvironmentVariable = "SCRIBE_TEXT_SCALE_FACTOR";

    private readonly Dispatcher _dispatcher;
    private readonly UISettings _settings = new();
    private bool _disposed;

    public TextScaleService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Factor = ReadFactor();
    }

    public event EventHandler? Changed;

    public static double CurrentFactor { get; private set; } = 1;

    public double Factor { get; private set; }

    public void Start()
    {
        Apply();
        _settings.TextScaleFactorChanged += OnTextScaleFactorChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _settings.TextScaleFactorChanged -= OnTextScaleFactorChanged;
        _disposed = true;
    }

    private void OnTextScaleFactorChanged(UISettings sender, object args)
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        _dispatcher.BeginInvoke(Apply);
    }

    private void Apply()
    {
        if (_disposed)
        {
            return;
        }

        Factor = ReadFactor();
        CurrentFactor = Factor;
        ApplyResources(Application.Current.Resources, Factor);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private double ReadFactor()
    {
        if (double.TryParse(
                Environment.GetEnvironmentVariable(OverrideEnvironmentVariable),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var forced))
        {
            return TextScale.NormalizeFactor(forced);
        }

        return TextScale.NormalizeFactor(_settings.TextScaleFactor);
    }

    private static void ApplyResources(ResourceDictionary resources, double factor)
    {
        resources["ScribeFontCaption"] = TextScale.Apply(12, factor);
        resources["ScribeFontBody"] = TextScale.Apply(14, factor);
        resources["ScribeFontBodyLarge"] = TextScale.Apply(16, factor);
        resources["ScribeFontSubtitle"] = TextScale.Apply(20, factor);
        resources["ScribeFontIconLarge"] = TextScale.Apply(22, factor);
        resources["ScribeFontTitle"] = TextScale.Apply(26, factor);
        resources["ScribeFontDisplay"] = TextScale.Apply(68, factor);

        resources["ControlContentThemeFontSize"] = TextScale.Apply(14, factor);
        resources["TextControlThemeFontSize"] = TextScale.Apply(14, factor);
        resources["DefaultDataGridFontSize"] = TextScale.Apply(14, factor);
        resources["TitleBarThemeFontSize"] = TextScale.Apply(12, factor);
        resources["InfoBarTitleThemeFontSize"] = TextScale.Apply(14, factor);
        resources["InfoBarMessageThemeFontSize"] = TextScale.Apply(14, factor);
        TextScaleControlStyles.Apply(resources, factor);
    }
}
