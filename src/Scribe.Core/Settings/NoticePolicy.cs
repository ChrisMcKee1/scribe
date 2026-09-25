namespace Scribe.Core.Settings;

public enum NoticeSeverity
{
    Success,
    Information,
    Warning,
    Error,
}

public static class NoticePolicy
{
    public static TimeSpan? AutoCloseAfter(NoticeSeverity severity) => severity switch
    {
        NoticeSeverity.Success => TimeSpan.FromSeconds(8),
        NoticeSeverity.Information => TimeSpan.FromSeconds(10),
        NoticeSeverity.Warning => null,
        NoticeSeverity.Error => null,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null),
    };
}
