using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace HWEncParamViewer.Core;

// frame単位経路の規則。既存の全列取得は後続の入口patchでこの形へ移す。
// cache/有限sink/例外窓──所属参照──→frameowner←──長期参照──表示/作業/UI待機/anchor
// snapshot/候補/通知──metadataだけで選択──→画素を読む1枚だけ取得して処理へ渡す
// 生存するcacheのgate内、producerのcallback、長期所有中は通常Retainを使う。
// gate外で選択したdescriptorはTryCaptureで取得し、解放済みならcache missへ戻す。
// Try成功前に選択の副作用をcommitしない。再queryは呼出元で有限回にし、無制限retryはしない。
// ID0/null/Completedの例外窓は設定時1回だけ長期所有し、移動やrefreshで取り直さない。
// 最後のReleaseで画素配列を切り離す。metadataはその後も読めるが、画素は読めない。
// 外部提供の配列は再利用しない。通常画面の読取で借りた配列だけを、最終所有の解放後に返す。
internal sealed class RawOwnershipScope
{
    private readonly object _gate = new();
    private readonly Dictionary<long, RawFrameOwner> _live = new();
    private readonly RawBufferPool? _pool;
    internal RawOwnershipScope(RawBufferPool? pool = null) => _pool = pool;
    internal void ClearFreeBuffers() => _pool?.ClearFreeBuffers();
    internal void ClosePool() => _pool?.Close();
    internal void SetReturnedBufferPoisonForTesting(byte value) => _pool?.SetReturnedBufferPoisonForTesting(value);
    internal RawBufferPool.Snapshot CapturePool() => _pool?.Capture() ?? RawBufferPool.Disabled;
    internal RawFrameLease RentReader(Y4mHeader header, double? xts, double? xdur, long runId)
    {
        if (_pool == null) return Create(header, xts, xdur, new byte[header.FrameBytes], runId, "Reader");
        byte[] buffer = _pool.Rent(header.FrameBytes);
        try { return CreateCore(header, xts, xdur, buffer, runId, "Reader", _pool); }
        catch { _pool.Return(buffer); throw; }
    }
    private long _nextFrame, _nextRun, _newOwners, _newBytes, _released, _releasedBytes, _liveBytes, _peak, _retains, _tokenReleases;
    private readonly Dictionary<string, TryRetainCounts> _tryByCaller = new(StringComparer.Ordinal);
    private long _tryAttempts, _trySuccesses, _tryFailures;
    private sealed class TryRetainCounts
    {
        internal long Attempts, Successes, Failures;
    }
    internal sealed record TryRetainCallerState(long Attempts, long Successes, long Failures);
    internal sealed record OwnerState(long FrameId, long RawRunId, long Bytes, long Tokens, IReadOnlyDictionary<string, long> Kinds);
    internal sealed record State(bool Owned, long NewOwners, long NewBytes, long FinalReleases, long FinalReleasedBytes,
        long LiveOwners, long LiveBytes, long PeakBytes, long LiveTokens, IReadOnlyDictionary<string, long> OwnerKinds,
        IReadOnlyList<OwnerState> Live, long TotalRetains, long TotalTokenReleases,
        long TryRetainAttempts, long TryRetainSuccesses, long TryRetainFailures,
        IReadOnlyDictionary<string, TryRetainCallerState> TryRetainsByCaller)
    {
        public bool IsEmpty => LiveOwners == 0 && LiveTokens == 0;
        public RawBufferPool.Snapshot? Pool { get; init; }
    }
    internal long NewRunId()
    {
        lock (_gate)
            return ++_nextRun;
    }
    internal RawFrameLease Create(Y4mHeader header, double? xts, double? xdur, byte[] buffer, long runId, string kind)
        => CreateCore(header, xts, xdur, buffer, runId, kind, null);
    private RawFrameLease CreateCore(Y4mHeader header, double? xts, double? xdur, byte[] buffer, long runId, string kind,
        RawBufferPool? returnPool)
    {
        lock (_gate)
        {
            var owner = new RawFrameOwner(this, ++_nextFrame, runId, buffer, returnPool);
            var frame = new Y4mFrame(Freeze(header), xts, xdur, owner, true);
            owner.Frame = frame;
            // 配列を返してよい失敗と公開済みの所有を分けるため、初期所有の確保を登録より先に終える。
            var initial = new RawFrameLease(frame, owner, kind);
            owner.Kinds.Add(kind, 1);
            owner.Tokens = 1;
            _live.Add(owner.FrameId, owner);
            ++_retains;
            ++_newOwners;
            _newBytes += buffer.LongLength;
            _liveBytes += buffer.LongLength;
            _peak = Math.Max(_peak, _liveBytes);
            return initial;
        }
    }
    private RawFrameLease RetainCore(RawFrameOwner owner, string kind)
    {
        // 初期Reader tokenも含む、成功した所有取得の累積回数。
        var lease = new RawFrameLease(owner.Frame!, owner, kind);
        owner.Kinds[kind] = owner.Kinds.GetValueOrDefault(kind) + 1;
        ++_retains;
        ++owner.Tokens;
        return lease;
    }
    internal RawFrameLease Retain(RawFrameOwner owner, string kind)
    {
        lock (_gate)
        {
            if (!_live.ContainsKey(owner.FrameId) || owner.Buffer == null)
                throw new ObjectDisposedException(nameof(RawFrameLease));
            return RetainCore(owner, kind);
        }
    }
    internal bool TryRetain(RawFrameOwner owner, string kind, [NotNullWhen(true)] out RawFrameLease? lease)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!ReferenceEquals(owner.Scope, this))
            throw new ArgumentException("別の所有scopeのframeは取得できません。", nameof(owner));
        lock (_gate)
        {
            ++_tryAttempts;
            if (!_tryByCaller.TryGetValue(kind, out var caller))
                _tryByCaller.Add(kind, caller = new());
            ++caller.Attempts;
            // 最終Releaseと同じgate内で確認から取得まで行い、解放済みownerを復活させない。
            if (!_live.TryGetValue(owner.FrameId, out var live) || !ReferenceEquals(live, owner)
                || owner.Buffer == null || owner.Tokens <= 0)
            {
                ++_tryFailures;
                ++caller.Failures;
                lease = null;
                return false;
            }
            lease = RetainCore(owner, kind);
            ++_trySuccesses;
            ++caller.Successes;
            return true;
        }
    }
    internal void Release(RawFrameOwner owner, string kind)
    {
        byte[]? returned = null;
        lock (_gate)
        {
            if (owner.Tokens <= 0)
                throw new InvalidOperationException("raw所有権の二重解放です。");
            ++_tokenReleases;
            if (--owner.Kinds[kind] == 0)
                owner.Kinds.Remove(kind);
            if (--owner.Tokens != 0)
                return;
            returned = owner.Buffer;
            owner.Buffer = null;
            _live.Remove(owner.FrameId);
            ++_released;
            _releasedBytes += owner.Bytes;
            _liveBytes -= owner.Bytes;
        }
        // 元の所有を切り離したあとだけ返し、検証用番兵や配列保持の排他を所有の排他へ持ち込まない。
        if (returned != null && owner.ReturnPool is { } pool) pool.Return(returned);
    }
    internal State Capture()
    {
        lock (_gate)
        {
            var kinds = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var owner in _live.Values)
                foreach (var pair in owner.Kinds)
                    kinds[pair.Key] = kinds.GetValueOrDefault(pair.Key) + pair.Value;
            var tries = new ReadOnlyDictionary<string, TryRetainCallerState>(
                _tryByCaller.ToDictionary(pair => pair.Key,
                    pair => new TryRetainCallerState(pair.Value.Attempts, pair.Value.Successes, pair.Value.Failures), StringComparer.Ordinal));
            return new(true, _newOwners, _newBytes, _released, _releasedBytes, _live.Count, _liveBytes, _peak,
                _live.Values.Sum(x => x.Tokens), kinds, _live.Values.Select(x => new OwnerState(x.FrameId, x.RawRunId, x.Bytes, x.Tokens, new Dictionary<string, long>(x.Kinds))).ToArray(), _retains, _tokenReleases,
                _tryAttempts, _trySuccesses, _tryFailures, tries) { Pool = _pool?.Capture() };
        }
    }
    private static Y4mHeader Freeze(Y4mHeader h) => new()
    {
        Width = h.Width,
        Height = h.Height,
        FpsNum = h.FpsNum,
        FpsDen = h.FpsDen,
        Interlace = h.Interlace,
        SarNum = h.SarNum,
        SarDen = h.SarDen,
        Chroma = h.Chroma,
        BitDepth = h.BitDepth,
        ColorSpaceTag = h.ColorSpaceTag,
        ExtTags = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(h.ExtTags))
    };
}
internal sealed class RawFrameOwner(RawOwnershipScope scope, long frameId, long rawRunId, byte[] buffer,
    RawBufferPool? returnPool = null)
{
    internal readonly RawOwnershipScope Scope = scope;
    internal readonly long FrameId = frameId, RawRunId = rawRunId, Bytes = buffer.LongLength;
    internal byte[]? Buffer = buffer;
    internal readonly RawBufferPool? ReturnPool = returnPool;
    internal Y4mFrame? Frame;
    internal long Tokens;
    internal readonly Dictionary<string, long> Kinds = new(StringComparer.Ordinal);
    internal byte[] ReadBuffer() => Volatile.Read(ref Buffer) ?? throw new ObjectDisposedException(nameof(RawFrameLease));
}
internal sealed class RawFrameLease : IDisposable
{
    private Y4mFrame? _frame;
    private RawFrameOwner? _owner;
    private readonly string _kind;
    private readonly object _gate = new();
    internal RawFrameLease(Y4mFrame frame, RawFrameOwner? owner, string kind)
    {
        _frame = frame;
        _owner = owner;
        _kind = kind;
    }
    internal Y4mFrame Frame
    {
        get
        {
            lock (_gate)
                return _frame ?? throw new ObjectDisposedException(nameof(RawFrameLease));
        }
    }
    internal Y4mHeader Header => Frame.Header;
    internal double? Xts => Frame.Xts;
    internal double? Xdur => Frame.Xdur;
    internal long FrameId => Frame.FrameId;
    internal long RawRunId => Frame.RawRunId;
    internal ReadOnlyMemory<byte> Pixels => Frame.RawPixels;
    internal bool Owned => _owner != null;
    internal static RawFrameLease Capture(Y4mFrame frame, string ownerKind) => frame.RawOwner is { } owner
        ? owner.Scope.Retain(owner, ownerKind) : new(frame, null, ownerKind);
    internal static bool TryCapture(Y4mFrame frame, string ownerKind, [NotNullWhen(true)] out RawFrameLease? lease)
    {
        if (frame.RawOwner is not { } owner)
        {
            // legacyは従来のGC所有のまま。ownedの試行台帳には含めない。
            lease = new(frame, null, ownerKind);
            return true;
        }
        if (owner.Scope.TryRetain(owner, ownerKind, out lease))
            return true;
        if (PerfTrace.Enabled)
            PerfTrace.Write("raw.try_retain_failed", new { frame.FrameId, frame.RawRunId, caller = ownerKind });
        return false;
    }
    internal RawFrameLease Retain(string ownerKind)
    {
        lock (_gate)
            return Capture(_frame ?? throw new ObjectDisposedException(nameof(RawFrameLease)), ownerKind);
    }
    public void Dispose()
    {
        RawFrameOwner? owner;
        lock (_gate)
        {
            if (_frame == null)
                return;
            _frame = null;
            owner = _owner;
            _owner = null;
        }
        owner?.Scope.Release(owner, _kind);
    }
}
internal sealed class RawFrameSetLease : IDisposable
{
    private readonly object _gate = new();
    private RawFrameLease[]? _leases;
    private IReadOnlyList<Y4mFrame> _frames;
    private PreviewSegmentSnapshot? _segment;
    private RawFrameSetLease(IEnumerable<Y4mFrame> frames, string kind, PreviewSegmentSnapshot? segment)
    {
        var retained = new List<RawFrameLease>();
        var descriptors = frames.ToArray();
        try
        {
            foreach (var frame in descriptors)
                if (frame.RawOwner != null)
                    retained.Add(RawFrameLease.Capture(frame, kind));
        }
        catch
        {
            foreach (var lease in retained)
                lease.Dispose();
            throw;
        }
        _leases = retained.ToArray();
        _frames = Array.AsReadOnly(descriptors);
        _segment = segment == null ? null : segment with
        {
            Frames = _frames
        };
    }
    internal static RawFrameSetLease Capture(IEnumerable<Y4mFrame> frames, string kind) => new(frames, kind, null);
    internal static RawFrameSetLease Capture(PreviewSegmentSnapshot segment, string kind) => new(segment.Frames, kind, segment);
    private void Check()
    {
        if (_leases == null)
            throw new ObjectDisposedException(nameof(RawFrameSetLease));
    }
    internal IReadOnlyList<Y4mFrame> Frames
    {
        get
        {
            lock (_gate)
            {
                Check();
                return _frames;
            }
        }
    }
    internal PreviewSegmentSnapshot? Segment
    {
        get
        {
            lock (_gate)
            {
                Check();
                return _segment;
            }
        }
    }
    internal RawFrameSetLease Retain(string kind)
    {
        lock (_gate)
        {
            Check();
            return new(_frames, kind, _segment);
        }
    }
    internal RawFrameLease RetainOne(int index, string kind)
    {
        lock (_gate)
        {
            Check();
            return RawFrameLease.Capture(_frames[index], kind);
        }
    }
    // 任意の抜粋はindex原点が変わるため、元区間の時計metadataを流用しない。
    internal RawFrameSetLease RetainSubset(IEnumerable<int> indices, string kind)
    {
        lock (_gate)
        {
            Check();
            return new(indices.Select(i => _frames[i]).ToArray(), kind, null);
        }
    }
    public void Dispose()
    {
        RawFrameLease[]? leases;
        lock (_gate)
        {
            leases = _leases;
            _leases = null;
            _frames = Array.Empty<Y4mFrame>();
            _segment = null;
        }
        if (leases != null)
            foreach (var lease in leases)
                lease.Dispose();
    }
}
internal sealed class OwnedPreviewEnvelope : IDisposable
{
    private const string CompletionWindowKind = "NotificationCompletionWindow";
    private const string Id0WindowKind = "NotificationId0Window";
    private RawFrameSetLease? _raw;
    private RawFrameWindow? _finiteWindow;
    private readonly bool _hasFiniteWindow;
    internal PreviewFrameResult? FrameReady
    { get; }
    internal PreviewCompletion? Completed
    { get; }
    internal PreviewSegmentUpdate? SegmentUpdated
    { get; }
    internal OwnedPreviewEnvelope(PreviewFrameResult? frame = null, PreviewCompletion? completed = null, PreviewSegmentUpdate? update = null, string ownerKind = "Notification")
    {
        // sinkの可変listを閉じても、Postへ渡した借用metadata列は変化させない。
        if (completed != null)
            completed = completed with
            {
                Frames = Array.AsReadOnly(completed.Frames.ToArray())
            };
        FrameReady = frame;
        Completed = completed;
        SegmentUpdated = update;
        IEnumerable<Y4mFrame> frames = frame == null ? Array.Empty<Y4mFrame>() : new[] { frame.Frame };
        bool finiteCompletion = completed != null && completed.Segment is not { SegmentId: > 0 };
        if (finiteCompletion)
        {
            var finite = completed!;
            frames = frames.Concat(finite.Frames).Concat(finite.Segment?.Frames ?? Array.Empty<Y4mFrame>());
        }
        // 正IDの完了と更新はdescriptorだけを渡し、必要な救済画像は生存するcallback内で選ぶ。
        // ID0/nullの完了とID0更新だけは、UI受渡し中の有限窓を保護する。
        if (update != null && update.Segment.SegmentId <= 0)
            frames = frames.Concat(update.Segment.Frames);
        _hasFiniteWindow = finiteCompletion || update != null && update.Segment.SegmentId <= 0;
        var uniqueFrames = frames.Distinct<Y4mFrame>(ReferenceEqualityComparer.Instance).ToArray();
        if (_hasFiniteWindow)
            _finiteWindow = RawFrameWindow.Capture(uniqueFrames, finiteCompletion ? CompletionWindowKind : Id0WindowKind);
        else
            _raw = RawFrameSetLease.Capture(uniqueFrames, ownerKind);
    }
    private OwnedPreviewEnvelope(PreviewFrameResult? frame, PreviewCompletion? completed, PreviewSegmentUpdate? update,
        RawFrameSetLease? raw, RawFrameWindow? finiteWindow)
    {
        FrameReady = frame;
        Completed = completed;
        SegmentUpdated = update;
        _raw = raw;
        _finiteWindow = finiteWindow;
        _hasFiniteWindow = finiteWindow != null;
    }
    internal static bool TryCreateFrame(PreviewFrameResult value,
        [NotNullWhen(true)] out OwnedPreviewEnvelope? envelope)
    {
        ArgumentNullException.ThrowIfNull(value);
        envelope = null;
        if (!RawFrameWindow.TryCapture(new[] { value.Frame }, "Notification", out var window)) return false;
        // 選択した一枚の取得成功後だけ通知へ渡し、親のmetadata列を画素所有へ広げない。
        try { envelope = new(value, null, null, null, window); return true; }
        catch { window.Dispose(); throw; }
    }

    internal OwnedPreviewEnvelope Retain(string ownerKind)
    {
        ArgumentNullException.ThrowIfNull(ownerKind);
        if (_hasFiniteWindow)
        {
            var shared = (Volatile.Read(ref _finiteWindow) ?? throw new ObjectDisposedException(nameof(OwnedPreviewEnvelope))).Share();
            // 有限窓とTry取得した一枚の所有は最初の通知だけで取得し、共有先のkindへ付け替えない。
            try { return new(FrameReady, Completed, SegmentUpdated, null, shared); }
            catch { shared.Dispose(); throw; }
        }
        var retained = (Volatile.Read(ref _raw) ?? throw new ObjectDisposedException(nameof(OwnedPreviewEnvelope))).Retain(ownerKind);
        // 生存確認で取得した所有をそのまま渡し、同じframe列をconstructorで再取得しない。
        try { return new(FrameReady, Completed, SegmentUpdated, retained, null); }
        catch { retained.Dispose(); throw; }
    }
    // 元の通知を閉じても画素を失わないよう、表示側へ有限窓の共有所有を渡す。
    internal RawFrameWindow? ShareFiniteWindow()
    {
        if (_hasFiniteWindow)
            return (Volatile.Read(ref _finiteWindow) ?? throw new ObjectDisposedException(nameof(OwnedPreviewEnvelope))).Share();
        _ = Volatile.Read(ref _raw) ?? throw new ObjectDisposedException(nameof(OwnedPreviewEnvelope));
        return null;
    }
    public void Dispose()
    {
        Interlocked.Exchange(ref _raw, null)?.Dispose();
        Interlocked.Exchange(ref _finiteWindow, null)?.Dispose();
    }
}
internal sealed class OwnedNavigationLease : IDisposable
{
    private RawFrameSetLease? _raw;
    internal IReadOnlyList<PreviewFrameReference> References
    { get; }
    internal OwnedNavigationLease(IReadOnlyList<PreviewFrameReference> references, string kind)
    {
        References = references;
        _raw = RawFrameSetLease.Capture(references.SelectMany(x => x.Segment.Frames).Distinct<Y4mFrame>(ReferenceEqualityComparer.Instance), kind);
    }
    public void Dispose() => Interlocked.Exchange(ref _raw, null)?.Dispose();
}
internal interface IRawOwnedSink : IPreviewSink
{
}
internal interface IRawOwnedBackend
{
    Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink, RawOwnershipScope scope, CancellationToken cancellationToken = default);
}
