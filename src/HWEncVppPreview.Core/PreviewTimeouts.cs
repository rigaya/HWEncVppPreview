namespace HWEncVppPreview.Core;

/// <summary>起動、要求、終了で独立した期限。内部でのみ無限の期限を選べる。</summary>
internal sealed record PreviewTimeouts
{
    internal static PreviewTimeouts Default { get; } = new();

    internal static PreviewTimeouts Infinite { get; } = new()
    {
        Ready = Timeout.InfiniteTimeSpan,
        Probe = Timeout.InfiniteTimeSpan,
        Cancel = Timeout.InfiniteTimeSpan,
        Stop = Timeout.InfiniteTimeSpan,
        Input = Timeout.InfiniteTimeSpan,
        InputCleanup = Timeout.InfiniteTimeSpan,
        Watchdog = Timeout.InfiniteTimeSpan,
        DisposeGrace = Timeout.InfiniteTimeSpan,
        ControllerDispose = Timeout.InfiniteTimeSpan,
        Metadata = Timeout.InfiniteTimeSpan,
        ViewModelDispose = Timeout.InfiniteTimeSpan,
        CliHelp = Timeout.InfiniteTimeSpan,
        HelpCleanup = Timeout.InfiniteTimeSpan,
    };

    internal TimeSpan Ready { get; init; } = TimeSpan.FromSeconds(3);
    internal TimeSpan Probe { get; init; } = TimeSpan.FromSeconds(3);
    internal TimeSpan Cancel { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan Stop { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan Input { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan InputCleanup { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan Watchdog { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan DisposeGrace { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan ControllerDispose { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan Metadata { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan ViewModelDispose { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan CliHelp { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan HelpCleanup { get; init; } = TimeSpan.FromSeconds(5);

    internal static TimeSpan Finite(TimeSpan? value, TimeSpan fallback, string parameterName, bool allowZero = false)
    {
        TimeSpan timeout = value ?? fallback;
        if (timeout < TimeSpan.Zero || (!allowZero && timeout == TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(parameterName);
        return timeout;
    }

    internal static TimeSpan Validate(TimeSpan value, string parameterName, bool allowZero = false)
        => value == Timeout.InfiniteTimeSpan ? value : Finite(value, value, parameterName, allowZero);
}
