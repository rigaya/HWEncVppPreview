using System.Diagnostics;
using System.Text;

namespace HWEncVppPreview.Cli;

internal sealed record HelpProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>取得失敗を空helpと区別し、回収の失敗も診断へ残す。</summary>
internal sealed class HelpCaptureException(string message, Exception innerException, bool timedOut,
    int? encoderExitCode, string standardError, string? cleanupError) : Exception(message, innerException)
{
    internal bool TimedOut { get; } = timedOut;
    internal int? EncoderExitCode { get; } = encoderExitCode;
    internal string StandardError { get; } = standardError;
    internal string? CleanupError { get; } = cleanupError;
}

/// <summary>helpの終了と両パイプを同じ期限で待ち、自分の子だけを有界に回収する。</summary>
internal static class HelpProcessCapture
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    internal static ProcessStartInfo StartInfo(string encoder)
    {
        var start = new ProcessStartInfo(encoder);
        start.ArgumentList.Add("--help");
        return start;
    }

    internal static async Task<HelpProcessResult> CaptureAsync(ProcessStartInfo start, TimeProvider clock,
        TimeSpan timeout, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.CreateNoWindow = true;
        var process = new Process { StartInfo = start };
        Task<string>? stdout = null, stderr = null;
        Task? exited = null, kill = null;
        bool started = false;
        Exception? failure = null;
        string? cleanupError = null;
        int? encoderExitCode = null;
        try
        {
            if (!process.Start()) throw new IOException("helpの子プロセスを開始できません。");
            started = true;
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            // Dispose済みProcessに対する再待機を避け、終了待ちは所有者が一度だけ作る。
            exited = process.WaitForExitAsync();
            await Task.WhenAll(exited, stdout, stderr).WaitAsync(timeout, clock, token).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            if (started && (exited?.IsCompleted != true || stdout?.IsCompleted != true || stderr?.IsCompleted != true))
            {
                kill = Task.Run(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                });
                try
                {
                    await Task.WhenAll(kill, exited ?? Task.CompletedTask, stdout ?? Task.CompletedTask,
                        stderr ?? Task.CompletedTask).WaitAsync(CleanupTimeout, clock).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupError = exception.Message;
                    // 親が先に終了して孫がpipeを持つ場合も、取得側の返却を無期限に止めない。
                    try { process.StandardOutput.Dispose(); } catch (Exception) { }
                    try { process.StandardError.Dispose(); } catch (Exception) { }
                }
            }
            if (started)
            {
                try { if (process.HasExited) encoderExitCode = process.ExitCode; }
                catch (InvalidOperationException) { }
            }
            var pending = new Task?[] { exited, stdout, stderr, kill }.Where(task => task is not null).Cast<Task>().ToArray();
            Task all = Task.WhenAll(pending);
            if (all.IsCompleted)
            {
                _ = all.Exception;
                process.Dispose();
            }
            else
            {
                // 回収上限後のpipe継続とDisposeを競合させず、遅れて完了した時に所有者が破棄する。
                _ = all.ContinueWith(completed => { _ = completed.Exception; process.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        if (failure is OperationCanceledException && token.IsCancellationRequested)
            throw new OperationCanceledException("helpの取得を取り消しました。", failure, token);
        throw new HelpCaptureException("helpの取得に失敗しました: " + failure!.Message, failure,
            failure is TimeoutException, encoderExitCode, stderr?.IsCompletedSuccessfully == true ? stderr.Result : "", cleanupError);
    }
}
