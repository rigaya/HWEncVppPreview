using System.Diagnostics;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Testing;

internal static class TestProcesses
{
    internal static ServerBackend CreateServerBackend(ServerRestartPolicy? restartPolicy = null,
        Action<ProcessStartInfo>? configureStartInfo = null)
        => new(PreviewTimeouts.Infinite, TimeProvider.System, restartPolicy, configureStartInfo);

    internal static InputProbe CreateInputProbe(Action<ProcessStartInfo>? configureStartInfo = null)
        => new(PreviewTimeouts.Infinite, TimeProvider.System, configureStartInfo);

    internal static InputProbe CreateInputProbe(IPreviewBackend backend, TimeProvider clock,
        Action<ProcessStartInfo>? configureStartInfo = null)
        => new(PreviewTimeouts.Infinite, clock, configureStartInfo, backend);

    internal static InputProbe CreateProbeWithSimulatedDeadlines(IPreviewBackend backend, TimeProvider clock)
        => new(PreviewTimeouts.Infinite with { Input = TimeSpan.FromSeconds(30), InputCleanup = TimeSpan.FromSeconds(5) },
            clock, backend: backend, startDurationProcess: (_, _) => throw new InvalidOperationException("実プロセスは起動できません。"));

    internal static InputProbe CreateDurationProbe(PreviewTimeouts timeouts, TimeProvider clock, IPreviewBackend backend,
        Func<string, IEnumerable<string>, InputProbe.IDurationProcess> startDurationProcess)
        => new(timeouts, clock, backend: backend, startDurationProcess: startDurationProcess);

    internal static PreviewController CreateController(IPreviewBackend backend, FrameCache? cache = null,
        TimeSpan? debounce = null, IPreviewBackend? backgroundBackend = null, RawOwnershipScope? ownershipScope = null)
        => new(PreviewTimeouts.Infinite, TimeProvider.System, backend, cache, debounce, backgroundBackend, ownershipScope);
}
