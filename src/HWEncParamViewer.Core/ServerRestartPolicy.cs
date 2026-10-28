namespace HWEncParamViewer.Core;

/// <summary>常駐サーバの使い回しを終了する条件。</summary>
public sealed class ServerRestartPolicy
{
    public ServerRestartPolicy(int maxRequests = 200)
    {
        if (maxRequests <= 0) throw new ArgumentOutOfRangeException(nameof(maxRequests));
        MaxRequests = maxRequests;
    }

    public int MaxRequests { get; }

    public bool ShouldRestart(int completedRequests, int exitCode, bool protocolFailure = false,
        bool processExited = false, bool watchdog = false) =>
        completedRequests >= MaxRequests || exitCode is not (0 or -12 or -24)
        || protocolFailure || processExited || watchdog;
}
