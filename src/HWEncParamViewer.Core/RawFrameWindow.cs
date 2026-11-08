using System.Diagnostics.CodeAnalysis;

namespace HWEncParamViewer.Core;

/// <summary>有限区間の画素所有を一度だけ取得し、通知と表示側の保持先へ共有する。</summary>
internal sealed class RawFrameWindow : IDisposable
{
    private const int InitialReferenceCount = 1;
    private readonly SharedState _state;
    private int _disposed;

    private RawFrameWindow(SharedState state) => _state = state;

    // 記述子は所有を返した後も読めるため、画素の取得とは別の寿命で保持する。
    internal IReadOnlyList<Y4mFrame> Frames => _state.Frames;

    internal static RawFrameWindow Capture(IReadOnlyList<Y4mFrame> frames, string kind)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var raw = RawFrameSetLease.Capture(frames, kind);
        try { return new(new SharedState(raw.Frames, raw)); }
        catch { raw.Dispose(); throw; }
    }

    internal static bool TryCapture(IReadOnlyList<Y4mFrame> frames, string kind,
        [NotNullWhen(true)] out RawFrameWindow? window)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(kind);
        window = null;
        var descriptors = new Y4mFrame[frames.Count];
        LeaseSet? pending = new(descriptors.Length);
        try
        {
            for (int index = 0; index < descriptors.Length; ++index)
            {
                var frame = frames[index];
                if (!RawFrameLease.TryCapture(frame, kind, out var lease)) return false;
                pending.Add(index, lease);
                descriptors[index] = frame;
            }
            // Adoptは失敗時も所有を消費するため、呼出し前に返却責任を一度だけ移す。
            var transferred = pending;
            pending = null;
            window = Adopt(descriptors, transferred);
            return true;
        }
        finally { pending?.Dispose(); }
    }

    // cacheなどで取得済みの所有を消費し、記述子の固定中に失敗しても所有を取りこぼさない。
    internal static RawFrameWindow Adopt(IReadOnlyList<Y4mFrame> frames, IDisposable? ownership)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(frames);
            return new(new SharedState(Array.AsReadOnly(frames.ToArray()), ownership));
        }
        catch { ownership?.Dispose(); throw; }
    }

    internal RawFrameWindow Share()
    {
        lock (_state.Gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return ShareCore();
        }
    }

    internal RawFrameWindow? ShareIfSame(IReadOnlyList<Y4mFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        lock (_state.Gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            // 配列や時刻が同じでも別の画素かもしれないため、同じ順の同じframeだけを共有する。
            if (_state.Frames.Count != frames.Count) return null;
            for (int i = 0; i < frames.Count; ++i)
                if (!ReferenceEquals(_state.Frames[i], frames[i])) return null;
            return ShareCore();
        }
    }

    // 呼出元がGateを保持し、最後の返却と競合して所有を復活させないことを保証する。
    private RawFrameWindow ShareCore()
    {
        var shared = new RawFrameWindow(_state);
        ++_state.References;
        return shared;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IDisposable? ownership = null;
        lock (_state.Gate)
        {
            if (--_state.References == 0)
            {
                ownership = _state.Ownership;
                _state.Ownership = null;
            }
        }
        // 別の所有scopeの排他に入る可能性があるため、最後の返却は共有状態の排他外で行う。
        ownership?.Dispose();
    }

    // 部分取得でも成功取得でも同じ返却先を使い、途中失敗で画素を固定し続けない。
    private sealed class LeaseSet(int count) : IDisposable
    {
        private RawFrameLease?[]? _leases = new RawFrameLease?[count];
        internal void Add(int index, RawFrameLease lease) => _leases![index] = lease;
        public void Dispose()
        {
            var leases = Interlocked.Exchange(ref _leases, null);
            if (leases == null) return;
            foreach (var lease in leases) lease?.Dispose();
        }
    }

    private sealed class SharedState(IReadOnlyList<Y4mFrame> frames, IDisposable? ownership)
    {
        internal readonly object Gate = new();
        internal readonly IReadOnlyList<Y4mFrame> Frames = frames;
        internal IDisposable? Ownership = ownership;
        internal int References = InitialReferenceCount;
    }
}
