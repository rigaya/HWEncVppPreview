using System.Text.Json;
using HWEncVppPreview.Core;
using CliProgram = HWEncVppPreview.Cli.Program;

namespace HWEncVppPreview.Tests;

[Collection("Language")]
public sealed class ExceptionDiagnosticTests
{
    [Theory]
    [InlineData("ja", "例外の詳細:")]
    [InlineData("en", "Exception details:")]
    public void ChainPreservesTypesAndNativeMessages(string language, string label)
    {
        string previous = UiLanguage.Preference;
        try
        {
            UiLanguage.Set(language);
            var exception = new InvalidOperationException("外側の原文", new IOException("native\r\noriginal\rmessage\ncontinued"));
            Assert.Equal(new[] { "InvalidOperationException: 外側の原文", "IOException: native original message continued" }, ExceptionDiagnostics.Lines(exception));
            Assert.StartsWith(label + Environment.NewLine, ExceptionDiagnostics.Format(exception));
            Assert.Empty(ExceptionDiagnostics.Lines(null));
            Assert.Equal("", ExceptionDiagnostics.Format(null));
        }
        finally { UiLanguage.Set(previous); }
    }

    [Fact]
    public async Task ServerFrameFailurePreservesOriginalExceptionChain()
    {
        await using var backend = new ServerBackend();
        var root = new IOException("native sink cause");
        var original = new InvalidOperationException("sink frame failed", root);
        var request = new PreviewRequest
        {
            EncoderPath = BackendTests.FakeEncoderPath(), InputFile = "fake input.ts", Fps = 30,
            TargetSeconds = 0, PreMarginSeconds = 0, PostMarginSeconds = 0.1, InputArgs = ["--avhw"],
        };
        var failure = await Assert.ThrowsAsync<PreviewBackendException>(() =>
            backend.RunAsync(request, new ThrowingSink(original)).WaitWithDiagnosticsAsync(TestTimeouts.Hang));
        Assert.Equal(CoreText.ServerRequestFailed, failure.Message);
        Assert.Same(original, failure.InnerException);
        Assert.Same(root, failure.InnerException!.InnerException);
        Assert.Equal(new[]
        {
            "PreviewBackendException: " + CoreText.ServerRequestFailed,
            "InvalidOperationException: sink frame failed",
            "IOException: native sink cause",
        }, ExceptionDiagnostics.Lines(failure));
    }

    private sealed class ThrowingSink(Exception exception) : IPreviewSink
    {
        public void OnHeader(Y4mHeader header) { }
        public void OnOutput() { }
        public void OnFrame(Y4mFrame frame) => throw exception;
    }

    [Fact]
    public async Task CliArgumentFailureIncludesExceptionChainInJson()
    {
        using var output = new StringWriter();
        int code = await CliProgram.RunAsync(["--enc", "unused", "--input", "unused", "--time", "0", "--watchdog-seconds", "1e300"], output);
        Assert.Equal(2, code);
        using var json = JsonDocument.Parse(output.ToString());
        string[] lines = json.RootElement.GetProperty("exceptionChain").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Single(lines);
        Assert.StartsWith("ArgumentException: ", lines[0]);
        Assert.Equal("ArgumentException: " + json.RootElement.GetProperty("error").GetString(), lines[0]);
    }
}
