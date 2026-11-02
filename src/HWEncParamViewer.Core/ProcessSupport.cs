using System.Diagnostics;
using System.Text;

namespace HWEncParamViewer.Core;

internal static class ProcessSupport
{
    public static Process Start(string path, IEnumerable<string> arguments, Action<ProcessStartInfo>? configure)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        configure?.Invoke(info);
        return Process.Start(info) ?? throw new IOException(CoreText.EncoderStartFailed);
    }

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }

    public static Task ReapAsync(Process process) =>
        ReapBoundedAsync(process, TimeProvider.System, TimeSpan.FromSeconds(5));

    public static async Task<bool> ReapBoundedAsync(Process process, TimeProvider clock, TimeSpan timeout)
    {
        int? pid = null;
        try { pid = process.Id; } catch (InvalidOperationException) { }
        void Stage(string stage)
        {
            if (!PerfTrace.Enabled) return;
            PerfTrace.Write("process.reap.stage", new { stage, pid });
            PerfTrace.Flush();
        }
        var deadline = new CleanupDeadline(clock, timeout);
        Stage("kill_begin");
        // OSのプロセス操作自体が戻らない場合も、呼出し元を無期限に拘束しない。
        if (!await JoinAsync(Task.Run(() => Kill(process)), clock, deadline.Remaining, "process.kill", pid).ConfigureAwait(false))
            return false;
        Stage("kill_end");
        bool exited = await JoinAsync(process.WaitForExitAsync(), clock, deadline.Remaining, "process.exit", pid).ConfigureAwait(false);
        if (exited) Stage("wait_exit_end");
        return exited;
    }

    public static async Task<bool> JoinAsync(Task task, TimeProvider clock, TimeSpan timeout, string stage, int? pid = null)
    {
        try
        {
            await task.WaitAsync(timeout, clock).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            WarnTimeout(stage, pid);
            Observe(task);
            return false;
        }
    }

    public static void WarnTimeout(string stage, int? pid = null)
    {
        Trace.TraceWarning($"Process cleanup timed out: stage={stage}, pid={pid}");
        if (!PerfTrace.Enabled) return;
        PerfTrace.Write("process.cleanup_timeout", new { stage, pid, unreaped = true });
        PerfTrace.Flush();
    }

    public static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    public static void DisposeAfter(Process process, params Task[] tasks) => _ = DisposeAfterAsync(process, tasks);

    public static Task DisposeAfterAsync(Process process, params Task[] tasks)
    {
        // 未完了のpipe操作とDisposeを競合させず、遅れて完了した場合にも資源を解放する。
        Task cleanup = Task.WhenAll(tasks).ContinueWith(_ => process.Dispose(), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
        Observe(Task.WhenAll(tasks));
        Observe(cleanup);
        return cleanup;
    }

}

/// <summary>終了処理の各段階で共用する、注入時計による残り時間。</summary>
internal sealed class CleanupDeadline(TimeProvider clock, TimeSpan timeout)
{
    private readonly long _started = clock.GetTimestamp();
    public TimeSpan Remaining
    {
        get
        {
            TimeSpan remaining = timeout - clock.GetElapsedTime(_started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
}

/// <summary>パーサの読み取りより下で、実際のパイプ受信を監視する。</summary>
internal sealed class ProgressReadStream(Stream inner, Action onOutput) : Stream
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        if (read > 0) onOutput();
        return read;
    }
    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        if (read > 0) onOutput();
        return read;
    }
    public override int ReadByte()
    {
        int value = inner.ReadByte();
        if (value >= 0) onOutput();
        return value;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
