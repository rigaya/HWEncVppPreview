using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class ReaderCommandTests
{
    private static readonly string[] ColorOptions = ["--colormatrix", "--colorprim", "--transfer", "--colorrange", "--chromaloc"];
    private static readonly PreviewCapabilities Capabilities = new(ColorMatrix: true, ColorPrimaries: true,
        Transfer: true, ColorRange: true, ChromaLocation: true, VpyAssumeScriptDir: true);

    public static IEnumerable<object[]> ReaderCases()
    {
        foreach (string extension in new[] { ".mp4", ".ts", ".mkv", "", ".avs", ".vpy", ".avi", ".y4m", ".yuv", ".VPY", ".AvS" })
        {
            bool automaticAvFormat = extension is ".mp4" or ".ts" or ".mkv" or "";
            bool automaticVpy = extension.Equals(".vpy", StringComparison.OrdinalIgnoreCase);
            yield return [extension, Array.Empty<string>(), automaticAvFormat, automaticVpy];
            foreach (string reader in new[] { "--avhw", "--avsw", "--avcuvid", "--avqsv", "--avvce" })
                yield return [extension, new[] { reader }, true, false];
            foreach (string reader in new[] { "--vpy", "--vpy-mt" })
                yield return [extension, new[] { reader }, false, true];
            foreach (string reader in new[] { "--avs", "--avi", "--raw", "--y4m", "--sm" })
                yield return [extension, new[] { reader }, false, false];
        }
    }

    [Theory]
    [MemberData(nameof(ReaderCases))]
    public void 拡張子より明示readerを優先して対応する引数だけを追加する(string extension, string[] inputArgs,
        bool colorAuto, bool vpy)
    {
        var request = Request(extension, inputArgs);
        foreach (string[] args in new[] { CommandBuilder.BuildOneShot(request), CommandBuilder.BuildServer(request) })
        {
            foreach (string option in ColorOptions)
            {
                Assert.Equal(colorAuto, args.Contains(option));
                if (colorAuto) Assert.Equal("auto", args[Array.IndexOf(args, option) + 1]);
            }
            Assert.Equal(vpy, args.Contains("--vpy-assume-script-dir"));
            Assert.Equal(Capabilities, request.Capabilities);
        }
    }

    [Theory]
    [InlineData("--avhw", "--avs", false, false)]
    [InlineData("--avs", "--avsw", true, false)]
    [InlineData("--vpy", "--raw", false, false)]
    [InlineData("--raw", "--vpy-mt", false, true)]
    [InlineData("--vpy", "--avqsv", true, false)]
    [InlineData("--avsw", "--vpy", false, true)]
    public void readerはエンコーダと同じく最後の明示指定を使う(string first, string last, bool colorAuto, bool vpy)
    {
        var request = Request(".vpy", [first, last]);
        foreach (string[] args in new[] { CommandBuilder.BuildOneShot(request), CommandBuilder.BuildServer(request) })
        {
            foreach (string option in ColorOptions) Assert.Equal(colorAuto, args.Contains(option));
            Assert.Equal(vpy, args.Contains("--vpy-assume-script-dir"));
            Assert.True(Array.IndexOf(args, first) < Array.IndexOf(args, last));
        }
    }

    [Theory]
    [InlineData("  --vpy-assume-script-dir  Resolve paths.\n", true)]
    [InlineData("--vpy-assume-script-dir  Resolve paths.\n", true)]
    [InlineData("    example: --vpy-assume-script-dir\n", false)]
    [InlineData("  --vpy-assume-script-dir-extra  Unrelated.\n", false)]
    [InlineData("", false)]
    public void vpyの相対パス指定はhelpの見出しで対応を確認する(string text, bool supported)
    {
        var capabilities = CommandBuilder.CapabilitiesFromHelp(new HelpIndex(text));
        Assert.Equal(supported, capabilities.VpyAssumeScriptDir);
        foreach (string[] args in new[] { CommandBuilder.BuildOneShot(Request(".vpy", []) with { Capabilities = capabilities }),
            CommandBuilder.BuildServer(Request(".vpy", []) with { Capabilities = capabilities }) })
            Assert.Equal(supported, args.Contains("--vpy-assume-script-dir"));
    }

    [Fact]
    public void 非avformat入力でも利用者の明示した色指定は保持する()
    {
        var request = Request(".avs", ["--device", "1", "--colormatrix", "bt709", "--colorrange", "full"]);
        foreach (string[] args in new[] { CommandBuilder.BuildOneShot(request), CommandBuilder.BuildServer(request) })
        {
            Assert.Equal("bt709", args[Array.IndexOf(args, "--colormatrix") + 1]);
            Assert.Equal("full", args[Array.IndexOf(args, "--colorrange") + 1]);
            Assert.DoesNotContain("auto", args);
        }
    }

    [Theory]
    [InlineData(".avs", false)]
    [InlineData(".vpy", true)]
    public async Task 入力検査にもreaderごとの自動指定を適用する(string extension, bool vpy)
    {
        var commands = new List<string[]>();
        await using var probe = new InputProbe(configureStartInfo: start => commands.Add(start.ArgumentList.ToArray()));
        await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input" + extension,
            Array.Empty<string>(), capabilities: Capabilities).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        string[] args = Assert.Single(commands);
        foreach (string option in ColorOptions) Assert.DoesNotContain(option, args);
        Assert.Equal(vpy, args.Contains("--vpy-assume-script-dir"));
    }

    [Theory]
    [InlineData(".vpy", "", true)]
    [InlineData(".VPY", "--vpy-mt", true)]
    [InlineData(".mp4", "--vpy", true)]
    [InlineData(".vpy", "--avsw", false)]
    [InlineData(".avs", "", false)]
    public async Task 長さ検査も同じreader規則でvpyの相対パス補助だけを付ける(string extension, string reader, bool expected)
    {
        var commands = new List<string[]>();
        await using var probe = new InputProbe(configureStartInfo: start => commands.Add(start.ArgumentList.ToArray()));
        string[] args = [.. string.IsNullOrEmpty(reader) ? Array.Empty<string>() : new[] { reader },
            "--fake-duration", "00:00:10.00"];
        var duration = await probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "input" + extension,
            args, capabilities: Capabilities).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Equal(TimeSpan.FromSeconds(10), duration);
        string[] command = Assert.Single(commands);
        Assert.Equal(expected, command.Contains("--vpy-assume-script-dir"));
        foreach (string color in ColorOptions) Assert.DoesNotContain(color, command);
    }

    [Fact]
    public async Task 長さ検査はhelpで確認できないvpy指定を付けず対応変更時は再検査する()
    {
        var commands = new List<string[]>();
        await using var probe = new InputProbe(configureStartInfo: start =>
        {
            // 相対パスを解決できない場合だけ長さが不明になる入力を模す。
            if (!start.ArgumentList.Contains("--vpy-assume-script-dir"))
                start.ArgumentList[start.ArgumentList.IndexOf("--fake-duration") + 1] = "N/A";
            commands.Add(start.ArgumentList.ToArray());
        });
        string[] args = ["--fake-duration", "00:00:10.00"];
        string encoder = BackendTests.FakeEncoderPath();
        Assert.Null(await probe.ProbeDurationAsync(encoder, "input.vpy", args)
            .WaitWithDiagnosticsAsync(TestTimeouts.Hang));
        Assert.DoesNotContain("--vpy-assume-script-dir", Assert.Single(commands));
        Assert.Equal(TimeSpan.FromSeconds(10), await probe.ProbeDurationAsync(encoder, "input.vpy", args,
            capabilities: Capabilities).WaitWithDiagnosticsAsync(TestTimeouts.Hang));
        Assert.Equal(2, commands.Count);
        Assert.Contains("--vpy-assume-script-dir", commands[1]);
        await probe.ProbeDurationAsync(encoder, "input.vpy", args, capabilities: Capabilities)
            .WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Equal(2, commands.Count);
    }

    private static PreviewRequest Request(string extension, string[] inputArgs) => new()
    {
        InputFile = @"X:\入力フォルダ\input" + extension, InputArgs = inputArgs,
        TargetSeconds = 0, Fps = 30, Capabilities = Capabilities,
    };
}
