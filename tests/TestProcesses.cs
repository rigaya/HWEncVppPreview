using System.Diagnostics;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Testing;

internal static class TestProcesses
{
    internal static ServerBackend CreateServerBackend(ServerRestartPolicy? restartPolicy = null,
        Action<ProcessStartInfo>? configureStartInfo = null)
        => new(PreviewTimeouts.Infinite, TimeProvider.System, restartPolicy, configureStartInfo);

    // 有限期限そのものの検証は、段階2でプロセスを使わない試験へ移す。
    internal static ServerBackend CreateServerBackendWithFiniteTimeouts(TimeSpan? readyTimeout = null,
        TimeSpan? cancelTimeout = null, TimeSpan? stopTimeout = null, TimeSpan? probeTimeout = null,
        Action<ProcessStartInfo>? configureStartInfo = null)
        => new(readyTimeout: readyTimeout, cancelTimeout: cancelTimeout, stopTimeout: stopTimeout,
            probeTimeout: probeTimeout, configureStartInfo: configureStartInfo);

    internal static InputProbe CreateInputProbe(Action<ProcessStartInfo>? configureStartInfo = null)
        => new(PreviewTimeouts.Infinite, TimeProvider.System, configureStartInfo);

    internal static InputProbe CreateInputProbe(IPreviewBackend backend, TimeProvider clock,
        Action<ProcessStartInfo>? configureStartInfo = null)
        => new(backend, clock, configureStartInfo);

    internal static InputProbe CreateInputProbeWithFiniteTimeout(TimeSpan timeout,
        Action<ProcessStartInfo>? configureStartInfo = null)
        => new(timeout: timeout, configureStartInfo: configureStartInfo);

    internal static PreviewController CreateController(IPreviewBackend backend, FrameCache? cache = null,
        TimeSpan? debounce = null, IPreviewBackend? backgroundBackend = null, RawOwnershipScope? ownershipScope = null)
        => new(PreviewTimeouts.Infinite, TimeProvider.System, backend, cache, debounce, backgroundBackend, ownershipScope);
}
