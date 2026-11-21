using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class CoreInputDefaultsTests
{
    [Fact]
    public void CoreもAppとCliと同じく入力引数の既定は空にする()
    {
        var request = new PreviewRequest { InputFile = "input.avs", Fps = 30 };
        Assert.Empty(request.InputArgs);
        Assert.DoesNotContain("--avhw", CommandBuilder.BuildServer(request));
    }

    [Fact]
    public async Task 画像と長さの検査はnullと空の入力引数で同じキャッシュを使う()
    {
        var backend = new RecordingProbeBackend();
        var starts = new List<string[]>();
        await using var probe = TestProcesses.CreateInputProbe(backend, TimeProvider.System, start => starts.Add(start.ArgumentList.ToArray()));
        var commands = backend.Commands;
        var capabilities = new PreviewCapabilities(VpyAssumeScriptDir: true);
        string encoder = BackendTests.FakeEncoderPath();
        var image = await probe.ProbeAsync(encoder, "image.vpy", capabilities: capabilities);
        Assert.Same(image, await probe.ProbeAsync(encoder, "image.vpy", Array.Empty<string>(), capabilities: capabilities));
        Assert.Single(commands);
        Assert.Null(await probe.ProbeDurationAsync(encoder, "duration.vpy", capabilities: capabilities));
        Assert.Null(await probe.ProbeDurationAsync(encoder, "duration.vpy", Array.Empty<string>(), capabilities: capabilities));
        Assert.Single(commands);
        Assert.Single(starts);
        Assert.All(commands.Concat(starts), args =>
        {
            Assert.DoesNotContain("--avhw", args);
            Assert.Contains("--vpy-assume-script-dir", args);
        });
    }
}
