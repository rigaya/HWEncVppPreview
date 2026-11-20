using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

internal static class TestViewModels
{
    internal static MainWindowViewModel Create(IViewModelServices? services = null,
        IPreviewBackend? backend = null, InputProbe? probe = null, FrameCache? cache = null,
        Func<Y4mFrame, CancellationToken, Task<byte[]>>? converter = null,
        IPreviewBackend? backgroundBackend = null, bool beforeEnabled = true,
        MainWindowViewModel.OwnedPreviewDependencies? owned = null, TimeSpan? debounceOverride = null,
        Func<string, string, CancellationToken, Task<string>>? readTextOverride = null)
        => new(services, TimeProvider.System, backend, probe, cache, converter, backgroundBackend,
            beforeEnabled, owned, debounceOverride, readTextOverride, PreviewTimeouts.Infinite);
}
