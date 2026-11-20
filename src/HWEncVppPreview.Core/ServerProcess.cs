using System.Diagnostics;

namespace HWEncVppPreview.Core;

internal interface IServerProcess : IDisposable
{
    int Id { get; }
    int ExitCode { get; }
    bool HasExited { get; }
    TextWriter StandardInput { get; }
    Stream StandardOutput { get; }
    TextReader StandardError { get; }
    Task WaitForExitAsync();
    Task<bool> ReapAsync(TimeProvider clock, TimeSpan timeout);
}

internal sealed class ServerProcess(Process process) : IServerProcess
{
    public int Id => process.Id;
    public int ExitCode => process.ExitCode;
    public bool HasExited => process.HasExited;
    public TextWriter StandardInput => process.StandardInput;
    public Stream StandardOutput => process.StandardOutput.BaseStream;
    public TextReader StandardError => process.StandardError;
    public Task WaitForExitAsync() => process.WaitForExitAsync();
    public Task<bool> ReapAsync(TimeProvider clock, TimeSpan timeout) => ProcessSupport.ReapBoundedAsync(process, clock, timeout);
    public void Dispose() => process.Dispose();
}
