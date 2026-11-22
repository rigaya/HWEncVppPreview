using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertOwnedRawReleased()
    {
        if (_rawOwnershipScope == null) return;
        var state = _rawOwnershipScope.Capture();
        string remaining = System.Text.Json.JsonSerializer.Serialize(state.OwnerKinds);
        string message = $"raw所有権: owners={state.LiveOwners}, tokens={state.LiveTokens}, bytes={state.LiveBytes}, kinds={remaining}";
        if (Volatile.Read(ref _shutdownIncomplete) != 0)
        {
            System.Diagnostics.Trace.TraceWarning("終了未完了のため台帳0判定を保留: {0}", message);
            return;
        }
        System.Diagnostics.Debug.Assert(state.IsEmpty, message);
    }

}
