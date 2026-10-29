using System.Text.Json;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _durationCancellation;
    private DurationIdentity? _durationAttempt;
    private long _durationGeneration;

    // 長さの寿命は入力単位とし、フィルタ編集や移動の要求世代とは分ける。
    private void CancelDuration()
    {
        lock (_taskLock)
        {
            ++_durationGeneration;
            _durationAttempt = null;
            _durationCancellation?.Cancel();
            _durationCancellation = null;
        }
    }

    private void ApplyInputInfo(InputInfo info)
    {
        if (info.Duration == null && HasDuration) info = info with { Duration = TimeSpan.FromSeconds(DurationSeconds) };
        _inputInfo = info;
        HasDuration = info.Duration.HasValue;
        DurationSeconds = info.Duration?.TotalSeconds ?? 0;
        DurationText = info.Duration is { } duration ? FormatTime(duration.TotalSeconds, milliseconds: false) : "";
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
                var duration = await _probe.ProbeDurationAsync(encoder, input, inputArgs, cancellation.Token).ConfigureAwait(false);
                await PostAsync(() =>
                {
                    if (_disposed || generation != _durationGeneration || identity != _durationAttempt || duration == null || HasDuration) return;
                    // 画像や目標時刻には触れず、後から得られた長さだけを反映する。
                    HasDuration = true;
                    DurationSeconds = duration.Value.TotalSeconds;
                    DurationText = FormatTime(DurationSeconds, milliseconds: false);
                    if (_inputInfo != null) _inputInfo = _inputInfo with { Duration = duration };
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
