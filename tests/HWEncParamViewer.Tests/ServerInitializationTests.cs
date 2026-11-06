using System.Diagnostics;
using System.Text.Json;
using HWEncParamViewer.Core;
namespace HWEncParamViewer.Tests;

public sealed class ServerInitializationTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ActualRequestWaitsBehindInitializationWithoutCancellingIt()
    {
        using var files = new Files();
        await using var backend = new ServerBackend(configureStartInfo: info => files.Configure(info));
        Task initialization = backend.InitializeEncoderAsync(BackendTests.FakeEncoderPath());
        await files.WaitReadyAsync();
        var sink = new Sink();
        Task<PreviewRunResult> actual = backend.RunAsync(Request(), sink);
        Assert.False(initialization.IsCompleted);
        Assert.False(actual.IsCompleted);
        Assert.Equal(0, sink.Frames);
        await File.WriteAllTextAsync(files.Release, "");
        await initialization.WaitAsync(Safety);
        Assert.Equal(0, (await actual.WaitAsync(Safety)).ExitCode);
        Assert.Equal(2, sink.Frames);
        var commands = File.ReadAllLines(files.Log).Select(line => JsonDocument.Parse(line))
            .Where(document => document.RootElement.GetProperty("cmd").GetString() == "run").ToArray();
        try
        {
            Assert.Equal(2, commands.Length);
            Assert.Contains("--y4m", commands[0].RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
            Assert.DoesNotContain("--y4m", commands[1].RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
        }
        finally { foreach (var command in commands) command.Dispose(); }
    }

    [Fact]
    public async Task DisposeDuringInitializationReapsItsProcess()
    {
        using var files = new Files();
        var backend = new ServerBackend(configureStartInfo: info => files.Configure(info));
        Task initialization = backend.InitializeEncoderAsync(BackendTests.FakeEncoderPath());
        await files.WaitReadyAsync();
        int pid = int.Parse(await File.ReadAllTextAsync(files.Ready));
        await backend.DisposeAsync().AsTask().WaitAsync(Safety);
        await initialization.WaitAsync(Safety);
        AssertStopped(pid);
    }

    [Fact]
    public async Task EncoderChangeCancelsOldInitializationAndUsesNewSession()
    {
        using var files = new Files();
        int starts = 0;
        await using var backend = new ServerBackend(configureStartInfo: info => files.Configure(info, hold: ++starts == 1));
        string encoder = BackendTests.FakeEncoderPath();
        Task old = backend.InitializeEncoderAsync(encoder);
        await files.WaitReadyAsync();
        int pid = int.Parse(await File.ReadAllTextAsync(files.Ready));
        string alternate = Path.GetDirectoryName(encoder) + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar + Path.GetFileName(encoder);
        await backend.InitializeEncoderAsync(alternate).WaitAsync(Safety);
        await old.WaitAsync(Safety);
        Assert.Equal(2, starts);
        AssertStopped(pid);
    }

    [Fact]
    public async Task InitializationFailureDoesNotFailTheActualRequest()
    {
        using var files = new Files();
        await using var backend = new ServerBackend(configureStartInfo: info =>
        {
            files.Configure(info, hold: false);
            info.Environment["FAKE_ENCODER_INITIALIZE_RC"] = "-24";
        });
        await backend.InitializeEncoderAsync(BackendTests.FakeEncoderPath()).WaitAsync(Safety);
        var sink = new Sink();
        Assert.Equal(0, (await backend.RunAsync(Request(), sink).WaitAsync(Safety)).ExitCode);
        Assert.Equal(2, sink.Frames);
    }

    [Fact]
    public async Task ConcurrentWarmupCreatorsShareOneCompleteFixedFile()
    {
        var method = typeof(ServerBackend).GetMethod("CreateWarmupInputAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Task<string>[] creations = Enumerable.Range(0, 8).Select(_ => (Task<string>)method.Invoke(null, null)!).ToArray();
        string[] paths = await Task.WhenAll(creations).WaitAsync(Safety);
        string path = Assert.Single(paths.Distinct());
        Assert.Equal("warmup-64x64.y4m", Path.GetFileName(path));
        byte[] bytes = await File.ReadAllBytesAsync(path);
        byte[] header = System.Text.Encoding.ASCII.GetBytes("YUV4MPEG2 W64 H64 F30:1 Ip A1:1 C420jpeg\nFRAME\n");
        Assert.Equal(header.Length + 64 * 64 * 3 / 2, bytes.Length);
        Assert.Equal(header, bytes[..header.Length]);
        Assert.All(bytes.Skip(header.Length).Take(64 * 64), value => Assert.Equal((byte)16, value));
        Assert.All(bytes.Skip(header.Length + 64 * 64), value => Assert.Equal((byte)128, value));
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(path)!, "warmup-64x64.y4m.*.tmp"), _ => true);
    }

    private static PreviewRequest Request() => new()
    {
        EncoderPath = BackendTests.FakeEncoderPath(), InputFile = "input.ts", Fps = 2,
        PreMarginSeconds = 0, PostMarginSeconds = 0.5,
    };
    private static void AssertStopped(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }
    private sealed class Sink : IPreviewSink
    {
        public int Frames;
        public void OnHeader(Y4mHeader header) { }
        public void OnFrame(Y4mFrame frame) => Frames++;
        public void OnOutput() { }
    }
    private sealed class Files : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "viewer-initialize-" + Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(_directory);
        public string Ready => Path.Combine(_directory, "ready");
        public string Release => Path.Combine(_directory, "release");
        public string Log => Path.Combine(_directory, "commands.jsonl");
        public void Configure(ProcessStartInfo info, bool hold = true)
        {
            info.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"] = Log;
            if (hold)
            {
                info.Environment["FAKE_ENCODER_INITIALIZE_READY"] = Ready;
                info.Environment["FAKE_ENCODER_INITIALIZE_RELEASE"] = Release;
            }
        }
        public async Task WaitReadyAsync()
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(_directory, "ready");
            watcher.Created += (_, _) => ready.TrySetResult();
            watcher.Changed += (_, _) => ready.TrySetResult();
            watcher.Renamed += (_, _) => ready.TrySetResult();
            watcher.EnableRaisingEvents = true;
            if (File.Exists(Ready)) ready.TrySetResult();
            await ready.Task.WaitAsync(Safety);
        }
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}
