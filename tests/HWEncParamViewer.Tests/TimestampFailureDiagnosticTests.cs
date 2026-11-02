using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.Tests;

[Collection("Language")]
public sealed class TimestampFailureDiagnosticTests
{
    [Theory]
    [InlineData(false, "protocol")]
    [InlineData(true, "protocol")]
    [InlineData(false, "io")]
    [InlineData(true, "io")]
    [InlineData(false, "timeout")]
    [InlineData(true, "timeout")]
    public async Task ReplayFailureKeepsClassificationAndAddsOneDiagnosticLine(bool continuous, string failureKind)
    {
        var failure = await RunFailureAsync(continuous, failureKind, legacy: true);
        Assert.Null(failure.Value.Result);
        Assert.Equal(2, failure.Backend.Calls);
        Assert.False(failure.Backend.LastRequest!.Capabilities.TimestampPassthrough);
        Assert.Equal(73, failure.Value.Request.CorrelationId);
        Assert.NotNull(failure.Value.Diagnostic);
        Assert.Equal(1, failure.Value.Diagnostic.Split('\n').Count(line => line.StartsWith("[preview] timestamp fallback:", StringComparison.Ordinal)));
        Assert.Contains("Xtsが増加していません", failure.Value.Diagnostic);
        AssertFailurePayload(failureKind, failure.Value, failure.Backend);
        if (failureKind != "io") Assert.Contains("native " + failureKind + " stderr", failure.Value.Diagnostic);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("io")]
    [InlineData("timeout")]
    public async Task HealthyFailurePayloadIsUnchanged(string failureKind)
    {
        var failure = await RunFailureAsync(false, failureKind, legacy: false);
        Assert.Null(failure.Value.Result);
        Assert.Null(failure.Value.Diagnostic);
        Assert.Equal(1, failure.Backend.Calls);
        AssertFailurePayload(failureKind, failure.Value, failure.Backend);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternalDiagnosticRemainsStableWhenDisplayLanguageIsEnglish(bool continuous)
    {
        string previous = UiLanguage.Preference;
        try
        {
            UiLanguage.Set("en");
            var failure = await RunFailureAsync(continuous, "protocol", legacy: true);
            Assert.Contains("要求内Xtsが増加していません", failure.Value.Diagnostic);
            Assert.Contains("native protocol stderr", failure.Value.Diagnostic);
            Assert.Equal("protocol failure", failure.Value.Reason);
        }
        finally { UiLanguage.Set(previous); }
    }

    private static void AssertFailurePayload(string kind, PreviewFailure failure, FailureBackend backend)
    {
        if (kind == "timeout")
        {
            Assert.Equal("出力が進まないためタイムアウトしました", failure.Reason);
            Assert.True(failure.TimedOut);
            Assert.Null(failure.Exception);
        }
        else
        {
            Assert.False(failure.TimedOut);
            Assert.Same(backend.ExpectedException, failure.Exception);
            Assert.Equal(backend.ExpectedException.Message, failure.Reason);
            if (kind == "protocol") Assert.Equal("native protocol stderr", ((PreviewBackendException)backend.ExpectedException).StandardError);
        }
    }

    private static async Task<(PreviewFailure Value, FailureBackend Backend)> RunFailureAsync(bool continuous, string kind, bool legacy)
    {
        var clock = new FakeTimeProvider();
        var backend = new FailureBackend(kind);
        await using var controller = new PreviewController(backend, timeProvider: clock, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        controller.Failed += value => failure = value;
        controller.Submit(new PreviewRequest
        {
            EncoderPath = "diagnostic-stub", InputFile = "input.ts", Fps = 30, TargetSeconds = 10,
            TimestampOriginSeconds = 1000, Capabilities = new(legacy), Continuous = continuous, CorrelationId = 73,
        });
        Task idle = controller.WaitForIdleAsync();
        await backend.FailureRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (kind == "timeout") clock.Advance(TimeSpan.FromSeconds(31));
        await idle.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(failure);
        return (failure, backend);
    }

    private sealed class FailureBackend : IPreviewBackend
    {
        private readonly string _kind;
        private readonly TaskCompletionSource<PreviewRunResult> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FailureRunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception ExpectedException { get; }
        public PreviewRequest? LastRequest { get; private set; }
        public int Calls { get; private set; }
        public FailureBackend(string kind)
        {
            _kind = kind;
            ExpectedException = kind == "protocol" ? new PreviewBackendException("protocol failure", "native protocol stderr") : new IOException("io failure");
        }
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            ++Calls;
            LastRequest = request;
            if (request.Capabilities.TimestampPassthrough)
            {
                var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 C420");
                double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
                for (int i = 0; i < 4; ++i)
                {
                    double time = 1000 + seek + (i == 3 ? 0 : i / 30d);
                    sink.OnFrame(new(header, time, 1.0 / 30, new byte[6]));
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            FailureRunStarted.TrySetResult();
            if (_kind == "timeout") return await _stopped.Task.ConfigureAwait(false);
            throw ExpectedException;
        }
        public ValueTask AbortAsync()
        {
            _stopped.TrySetResult(new(-12, "native timeout stderr", true));
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
