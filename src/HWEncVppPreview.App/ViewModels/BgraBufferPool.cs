using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.ViewModels;

// 再利用領域は旧貸出を含め2枠・256MiB以内。大画像は従来相当の一回限りの配列へ退避する。
internal sealed class BgraBufferPool(int maximumBuffers = 2, long maximumBytes = 256L << 20) : IDisposable
{
    private readonly record struct Format(int Width, int Height, int BitDepth, Y4mChroma Chroma,
        string? Tag, string? Matrix, string? Range, string? Primaries, string? Transfer, string? Location)
    {
        internal static Format From(Y4mHeader header) => new(header.Width, header.Height, header.BitDepth, header.Chroma,
            header.ColorSpaceTag, header.ColorMatrix, header.ColorRange, header.ColorPrimaries, header.Transfer, header.ChromaLocation);
    }
    internal sealed record State(int Buffers, int Leased, int Idle, long Bytes);
    private readonly object _sync = new();
    private readonly Stack<byte[]> _idle = new();
    private Format? _format;
    private int _buffers;
    private long _bytes, _generation, _retirementEpoch;
    private bool _disposed;
    internal State Capture()
    {
        lock (_sync) return new(_buffers, _buffers - _idle.Count, _idle.Count, _bytes);
    }
    internal long RetirementEpoch { get { lock (_sync) return _retirementEpoch; } }
    internal Lease Rent(Y4mHeader header, long? retirementEpoch = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        long pixels = (long)header.Width * header.Height;
        if (header.Width <= 0 || header.Height <= 0 || pixels > int.MaxValue / 4)
            throw new ArgumentException("BGRA画像の寸法が不正です。", nameof(header));
        long length = pixels * 4;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (retirementEpoch.HasValue && retirementEpoch.Value != _retirementEpoch)
                throw new OperationCanceledException("入力変更前の変換を確保前に棄却しました。");
            var format = Format.From(header);
            if (_format != format) { RetireCore(); _format = format; }
            byte[] buffer;
            bool pooled;
            if (_idle.TryPop(out byte[]? available)) { buffer = available; pooled = true; }
            else
            {
                pooled = _buffers < maximumBuffers && length <= maximumBytes && _bytes <= maximumBytes - length;
                buffer = new byte[(int)length];
                if (pooled) { ++_buffers; _bytes += length; }
            }
            return new(this, buffer, _generation, pooled);
        }
    }
    internal void Retire()
    {
        lock (_sync)
        {
            if (_disposed) return;
            ++_retirementEpoch; RetireCore(); _format = null;
        }
    }
    private void RetireCore()
    {
        ++_generation;
        while (_idle.TryPop(out byte[]? buffer)) { --_buffers; _bytes -= buffer.LongLength; }
    }
    private void Return(byte[] buffer, long generation, bool pooled)
    {
        if (!pooled) return;
        lock (_sync)
        {
            if (_disposed || generation != _generation) { --_buffers; _bytes -= buffer.LongLength; }
            else _idle.Push(buffer);
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; ++_retirementEpoch; RetireCore(); _format = null;
        }
    }
    internal sealed class Lease(BgraBufferPool owner, byte[] buffer, long generation, bool pooled) : IDisposable
    {
        private byte[]? _buffer = buffer;
        internal bool IsReusable => pooled;
        internal byte[] Buffer => Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(Lease));
        public void Dispose()
        {
            byte[]? value = Interlocked.Exchange(ref _buffer, null);
            if (value != null) owner.Return(value, generation, pooled);
        }
    }
}
