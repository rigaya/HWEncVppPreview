using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

// テストの完了時にVMの実終了を待つ。終了未完了を台帳0と混ぜない。
internal sealed class OwnedVmCheck(MainWindowViewModel vm, bool requireOwned = true,
    bool requirePixels = false, bool allowIncomplete = false) : IAsyncDisposable
{
    internal sealed record Result(bool Owned, bool Incomplete, long NewOwners, long LiveOwners, long LiveTokens);
    internal static ConcurrentQueue<Result> Results { get; } = new();
    internal static RawOwnershipScope? Scope(MainWindowViewModel target) => (RawOwnershipScope?)Field(target, "_rawOwnershipScope");
    internal static object? Field(object target, string name) => target.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);
    private bool _checked;
    public async ValueTask DisposeAsync()
    {
        if (_checked)
        {
            return;
        }
        _checked = true;
        await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        var scope = Scope(vm);
        bool incomplete = (int)Field(vm, "_shutdownIncomplete")! != 0;
        var state = scope?.Capture();
        Results.Enqueue(new(scope != null, incomplete, state?.NewOwners ?? 0, state?.LiveOwners ?? 0, state?.LiveTokens ?? 0));
        if (incomplete)
        {
            Assert.True(allowIncomplete, "想定外の終了未完了。台帳0検査とは別の結果として記録した。");
            return;
        }
        if (requireOwned)
        {
            Assert.NotNull(scope);
        }
        if (scope == null)
        {
            return;
        }
        Assert.True(state!.IsEmpty, JsonSerializer.Serialize(state));
        Assert.Equal(state.TotalRetains, state.TotalTokenReleases);
        if (requirePixels)
        {
            Assert.True(state.NewOwners > 0, "実owned frameが作成されていない。");
        }
    }
}
