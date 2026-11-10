using System.Diagnostics;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class ColorHeaderAndCommandTests
{
    private static readonly string[] ColorOptions = ["--colormatrix", "--colorprim", "--transfer", "--colorrange", "--chromaloc"];
    private static readonly PreviewCapabilities AllColors = new(ColorMatrix: true, ColorPrimaries: true,
        Transfer: true, ColorRange: true, ChromaLocation: true);
    private static readonly TimeSpan SafetyTimeout = TestTimeouts.Hang;

    [Fact]
    public void 色拡張は原文を保って各項目から取得できる()
    {
        var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 C420jpeg XCOLORRANGE=FULL XCOLORMATRIX=bt2020nc XCOLORPRIM=bt2020 XTRANSFER=smpte2084 XCHROMALOC=left XFUTURE=opaque");
        Assert.Equal("FULL", header.ColorRange);
        Assert.Equal("bt2020nc", header.ColorMatrix);
        Assert.Equal("bt2020", header.ColorPrimaries);
        Assert.Equal("smpte2084", header.Transfer);
        Assert.Equal("left", header.ChromaLocation);
        Assert.Equal("opaque", header.ExtTags["XFUTURE"]);
        Assert.Equal(6, header.FrameBytes);
    }

    [Fact]
    public void 拡張なしのヘッダは色の各項目が未指定になる()
    {
        var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 C420jpeg");
        Assert.Null(header.ColorRange);
        Assert.Null(header.ColorMatrix);
        Assert.Null(header.ColorPrimaries);
        Assert.Null(header.Transfer);
        Assert.Null(header.ChromaLocation);
    }

    [Fact]
    public void 未知の色情報も捨てず変換側の推定へ渡せる()
    {
        var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 XCOLORRANGE=FutureRange XCOLORMATRIX=future-matrix XCOLORPRIM= XFUTURE XTRANSFER=future:transfer XCHROMALOC=FutureLocation");
        Assert.Equal("FutureRange", header.ColorRange);
        Assert.Equal("future-matrix", header.ColorMatrix);
        Assert.Equal("", header.ColorPrimaries);
        Assert.Equal("future:transfer", header.Transfer);
        Assert.Equal("FutureLocation", header.ChromaLocation);
        Assert.Equal("", header.ExtTags["XFUTURE"]);
    }

    [Theory]
    [InlineData("--colormatrix")]
    [InlineData("--colorprim")]
    [InlineData("--transfer")]
    [InlineData("--colorrange")]
    [InlineData("--chromaloc")]
    public void helpの対応を項目別に確認して対応するautoだけを付ける(string option)
    {
        var help = new HelpIndex("  " + option + " <string> Input color metadata.\n");
        var request = Request() with { Capabilities = CommandBuilder.CapabilitiesFromHelp(help) };
        Assert.Equal(new PreviewCapabilities(ColorMatrix: option == "--colormatrix", ColorPrimaries: option == "--colorprim",
            Transfer: option == "--transfer", ColorRange: option == "--colorrange", ChromaLocation: option == "--chromaloc"), request.Capabilities);
        foreach (var args in new[] { CommandBuilder.BuildOneShot(request), CommandBuilder.BuildServer(request) })
        {
            foreach (string other in ColorOptions) Assert.Equal(option == other, args.Contains(other));
            Assert.Equal("auto", args[Array.IndexOf(args, option) + 1]);
        }
    }

    [Fact]
    public void helpがないか本文で名前を挙げただけなら色引数を足さない()
    {
        foreach (var capabilities in new[] { CommandBuilder.CapabilitiesFromHelp(null),
            CommandBuilder.CapabilitiesFromHelp(new HelpIndex("    example: --colormatrix auto\n  --colormatrix-extra <string> unrelated\n")) })
        {
            Assert.Equal(new PreviewCapabilities(), capabilities);
            foreach (string option in ColorOptions) Assert.DoesNotContain(option, CommandBuilder.BuildOneShot(Request() with { Capabilities = capabilities }));
        }
    }

    [Fact]
    public void 色の自動指定は入力とフィルタの明示指定より前に置く()
    {
        var request = Request() with
        {
            Capabilities = AllColors with { TimestampPassthrough = true, OpenClTaskThreads = true },
            InputArgs = ["--avhw", "--colormatrix", "bt709"],
            VppArgs = ["--colorrange", "full", "--vpp-colorspace", "matrix=bt709:bt601"],
        };
        foreach (var args in new[] { CommandBuilder.BuildOneShot(request), CommandBuilder.BuildServer(request) })
        {
            int input = Array.IndexOf(args, "--avhw");
            foreach (string option in ColorOptions)
            {
                int automatic = Array.IndexOf(args, option);
                Assert.InRange(automatic, 0, input - 1);
                Assert.Equal("auto", args[automatic + 1]);
            }
            Assert.Equal("bt709", args[Array.LastIndexOf(args, "--colormatrix") + 1]);
            Assert.Equal("full", args[Array.LastIndexOf(args, "--colorrange") + 1]);
            Assert.Equal("matrix=bt709:bt601", args[Array.IndexOf(args, "--vpp-colorspace") + 1]);
            Assert.Equal(new[] { "--timestamp-passthrough", "--opencl-task-threads", "0" }, args[^3..]);
        }
    }

    [Fact]
    public async Task 入力ヘッダの検査にも対応した色引数を付ける()
    {
        var commands = new List<string[]>();
        await using var probe = new InputProbe(configureStartInfo: start => commands.Add(start.ArgumentList.ToArray()));
        var info = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts",
            ["--avhw", "--colormatrix", "bt709"], capabilities: AllColors).WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(64, info.Width);
        var args = Assert.Single(commands);
        foreach (string option in ColorOptions) Assert.Equal("auto", args[Array.IndexOf(args, option) + 1]);
        Assert.Equal("bt709", args[Array.LastIndexOf(args, "--colormatrix") + 1]);
        Assert.Equal("300", args[Array.IndexOf(args, "--frames") + 1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 偽エンコーダから色情報を都度起動と常駐の両方で受け取れる(bool server)
    {
        await using IPreviewBackend backend = server ? new ServerBackend() : new OneShotBackend();
        var sink = new HeaderSink();
        var request = Request() with
        {
            Capabilities = AllColors,
            VppArgs = ["--fake-colormatrix", "smpte240m", "--fake-colorrange", "full", "--fake-colorprim", "bt2020",
                "--fake-transfer", "arib-std-b67", "--fake-chromaloc", "topleft"],
        };
        var result = await backend.RunAsync(request, sink).WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(0, result.ExitCode);
        var frame = Assert.Single(sink.Frames);
        Assert.Same(sink.Header, frame.Header);
        Assert.Equal("smpte240m", frame.Header.ColorMatrix);
        Assert.Equal("FULL", frame.Header.ColorRange);
        Assert.Equal("bt2020", frame.Header.ColorPrimaries);
        Assert.Equal("arib-std-b67", frame.Header.Transfer);
        Assert.Equal("topleft", frame.Header.ChromaLocation);
    }

    [Fact]
    public async Task 偽エンコーダのhelpから色オプションを検出できる()
    {
        var start = new ProcessStartInfo(BackendTests.FakeEncoderPath())
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        start.ArgumentList.Add("--help");
        start.Environment["FAKE_ENCODER_COLOR_CAPABILITIES"] = "1";
        start.Environment["FAKE_ENCODER_PREVIEW_CAPABILITIES"] = "0";
        start.Environment.Remove("FAKE_ENCODER_HELP_FILE");
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(AllColors, CommandBuilder.CapabilitiesFromHelp(new HelpIndex(await stdout)));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    private static PreviewRequest Request() => new()
    {
        EncoderPath = BackendTests.FakeEncoderPath(), InputFile = "input.ts", TargetSeconds = 0,
        PreMarginSeconds = 0, PostMarginSeconds = 0, Fps = 30,
    };

    private sealed class HeaderSink : IPreviewSink
    {
        public Y4mHeader? Header { get; private set; }
        public List<Y4mFrame> Frames { get; } = new();
        public void OnHeader(Y4mHeader header) => Header = header;
        public void OnFrame(Y4mFrame frame) => Frames.Add(frame);
        public void OnOutput() { }
    }
}
