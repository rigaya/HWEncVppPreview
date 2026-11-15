using System.Text.Json;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _durationCancellation;
    private DurationIdentity? _durationAttempt;
    private long _durationGeneration;
    private double? _reachedInputEndSeconds;

    // 長さの寿命は入力単位とし、フィルタ編集や移動の要求世代とは分ける。
    private void CancelDuration()
    {
        lock (_taskLock)
        {
            _durationAfterSeen = _durationBeforeSeen = _durationBeforeUnavailable = false;
            ++_durationGeneration;
            _durationAttempt = null;
            _reachedInputEndSeconds = null;
            _durationCancellation?.Cancel();
            _durationCancellation = null;
        }
    }

    private void ApplyInputInfo(InputInfo info)
    {
        if (info.Duration == null && HasDuration) info = info with { Duration = TimeSpan.FromSeconds(DurationSeconds) };
        if (_reachedInputEndSeconds is double actualEnd)
            info = info with { Duration = TimeSpan.FromSeconds(Math.Min(info.Duration?.TotalSeconds ?? actualEnd, actualEnd)) };
        _inputInfo = info;
        HasDuration = info.Duration.HasValue;
        DurationSeconds = info.Duration?.TotalSeconds ?? 0;
        DurationText = info.Duration is { } duration ? FormatTime(duration.TotalSeconds, milliseconds: false) : "";
    }

    // 申告長とは分けて、成功したAfterの自然EOFだけを入力単位で記憶する。
    private bool ApplyReachedInputEnd(RequestContext context, PreviewCompletion completion, PreviewSegmentSnapshot? segment)
    {
        if (context.Kind != RequestKind.After || completion.Result.ExitCode != 0 || completion.Result.IsCancelled
            || completion.Segment?.ReachedInputEnd != true || segment is not { Frames.Count: > 0 }) return false;
        var last = segment.Frames[^1];
        double start = segment.Clock.ToInputSeconds(last, segment.Frames.Count - 1, context.Request.Fps);
        double end = start + FrameSelector.Duration(last, context.Request.Fps);
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0
            || !PreviewSafeRange.IsKnownInputEnd(context.Request, segment, end)) return false;
        _reachedInputEndSeconds = Math.Min(_reachedInputEndSeconds ?? end, end);
        double duration = Math.Min(HasDuration ? DurationSeconds : end, _reachedInputEndSeconds.Value);
        HasDuration = true;
        DurationSeconds = duration;
        DurationText = FormatTime(duration, milliseconds: false);
        if (_inputInfo != null) _inputInfo = _inputInfo with { Duration = TimeSpan.FromSeconds(duration) };
        if (CurrentSeconds < end - PreviewSafeRange.ToleranceSeconds) return false;
        // 通常の時刻setterは新しいシークになるため、確定した同じ世代の表示位置だけを直す。
        SetTimeOnly(start);
        _lastIntentTarget = start;
        return true;
    }

    private bool _durationAfterSeen, _durationBeforeSeen, _durationBeforeUnavailable;

    private void QueueDurationAfterComparison(bool before, long generation)
    {
        if (before) _durationBeforeSeen = true;
        else _durationAfterSeen = true;
        TryQueueDuration();
    }

    private void BeforeUnavailableForDuration()
    {
        _durationBeforeUnavailable = true;
        TryQueueDuration();
    }

    private void TryQueueDuration()
    {
        // 画像の両側を反映してから長さを検査し、初回比較のGPU処理と競合させない。
        if (!_durationAfterSeen || !(_durationBeforeSeen || _durationBeforeUnavailable)) return;
        QueueDuration(EncoderPath, InputPath, OptionTextParser.ParseText(InputArgsText));
    }

    private void QueueDuration(string encoder, string input, string[] inputArgs)
    {
        if (HasDuration || _disposed) return;
        var identity = new DurationIdentity(encoder, input, JsonSerializer.Serialize(inputArgs));
        CancellationTokenSource cancellation;
        long generation;
        lock (_taskLock)
        {
            if (_disposed || identity == _durationAttempt) return;
            _durationAttempt = identity;
            generation = _durationGeneration;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _durationCancellation = cancellation;
        }
        Track(ReadDurationAsync());

        async Task ReadDurationAsync()
        {
            try
            {
                var duration = await _probe.ProbeDurationAsync(encoder, input, inputArgs, cancellation.Token, _capabilities).ConfigureAwait(false);
                await PostAsync(() =>
                {
                    if (_disposed || generation != _durationGeneration || identity != _durationAttempt || duration == null || HasDuration) return;
                    // 画像や目標時刻には触れず、後から得られた長さだけを反映する。
                    HasDuration = true;
                    DurationSeconds = Math.Min(duration.Value.TotalSeconds, _reachedInputEndSeconds ?? double.MaxValue);
                    DurationText = FormatTime(DurationSeconds, milliseconds: false);
                    if (_inputInfo != null) _inputInfo = _inputInfo with { Duration = TimeSpan.FromSeconds(DurationSeconds) };
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception)
            {
                // 長さが不明でもプレビューは利用できるので、画像側の診断を上書きしない。
            }
            finally
            {
                lock (_taskLock)
                {
                    if (ReferenceEquals(_durationCancellation, cancellation)) _durationCancellation = null;
                    cancellation.Dispose();
                }
            }
        }
    }

    private sealed record DurationIdentity(string Encoder, string Input, string Arguments);
}
