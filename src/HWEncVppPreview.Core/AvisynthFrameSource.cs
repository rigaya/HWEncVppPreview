using System.Runtime.InteropServices;

namespace HWEncVppPreview.Core;

/// <summary>番号指定の映像取得元。実装は呼び出し間でフレームを独立して保持する。</summary>
public interface IFrameNumberSource : IDisposable
{
    FrameNumberInfo Info { get; }
    KfmFrameProperties? GetProperties(int position);
    Y4mFrame GetFrame(int position);
}

/// <summary>AviSynthクリップの映像情報。</summary>
public sealed record FrameNumberInfo(int NumFrames, Y4mHeader Header);
/// <summary>KFMの画種と60fps単位の表示長。</summary>
public readonly record struct KfmFrameProperties(int FrameType, int Duration);

/// <summary>AmatsukazeのネイティブAPIを使う番号指定の取得元。</summary>
public sealed class AvisynthFrameSource : IFrameNumberSource
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInfo
    {
        public int NumFrames, FpsNum, FpsDen, Width, Height, SarW, SarH, BitDepth, PixelType;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CreateContext();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DeleteContext(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetError(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Open(nint context, nint path, out NativeInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetFrameNative(nint preview, int n, [Out] nint[] planes, [Out] int[] pitches);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetProps(nint preview, int n, out int type, out int duration);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Close(nint preview);

    private readonly object _gate = new();
    private readonly nint _library;
    private readonly nint _avisynth;
    private readonly DeleteContext _delete;
    private readonly GetError _error;
    private readonly GetFrameNative _frame;
    private readonly GetProps _props;
    private readonly Close _close;
    private nint _context, _preview;
    private bool _disposed;
    public FrameNumberInfo Info { get; }

    public AvisynthFrameSource(string directory, string avsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(avsPath);
        string libraryPath = Path.Combine(Path.GetFullPath(directory), OperatingSystem.IsWindows() ? "Amatsukaze.dll" : "libAmatsukaze.so");
        try
        {
            // 同梱版と同じABIを使い、システムの別版を読み込まないようにする。
            if (OperatingSystem.IsWindows())
                _avisynth = NativeLibrary.Load(Path.Combine(Path.GetFullPath(directory), "AviSynth.dll"));
            _library = NativeLibrary.Load(libraryPath);
            T Load<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
            var create = Load<CreateContext>("AMTContext_Create");
            _delete = Load<DeleteContext>("ATMContext_Delete");
            _error = Load<GetError>("AMTContext_GetError");
            var open = Load<Open>("FilterPreview_Open");
            _frame = Load<GetFrameNative>("FilterPreview_GetFrame");
            _props = Load<GetProps>("FilterPreview_GetKFMProps");
            _close = Load<Close>("FilterPreview_Close");
            _context = create();
            if (_context == 0) throw new InvalidOperationException("AMTContextの作成に失敗しました。");
            string fullPath = Path.GetFullPath(avsPath);
            nint path = OperatingSystem.IsWindows() ? Marshal.StringToHGlobalUni(fullPath) : Marshal.StringToCoTaskMemUTF8(fullPath);
            NativeInfo native;
            try { _preview = open(_context, path, out native); }
            finally { if (OperatingSystem.IsWindows()) Marshal.FreeHGlobal(path); else Marshal.FreeCoTaskMem(path); }
            if (_preview == 0) throw Error("avsを開けませんでした");
            if (native.NumFrames <= 0 || native.Width <= 0 || native.Height <= 0 || native.BitDepth is < 8 or > 16)
                throw new InvalidDataException("AviSynthの映像情報が不正です。");
            Y4mChroma chroma = native.PixelType switch { 1 => Y4mChroma.C420, 2 => Y4mChroma.C422, 3 => Y4mChroma.C444, _ => throw new InvalidDataException("未対応の画素形式です。") };
            var header = new Y4mHeader { Width = native.Width, Height = native.Height, FpsNum = native.FpsNum, FpsDen = native.FpsDen,
                SarNum = native.SarW, SarDen = native.SarH, BitDepth = native.BitDepth, Chroma = chroma,
                ColorSpaceTag = native.BitDepth == 8 ? native.PixelType switch { 1 => "420jpeg", 2 => "422", _ => "444" } : $"{(native.PixelType == 1 ? "420" : native.PixelType == 2 ? "422" : "444")}p{native.BitDepth}" };
            _ = header.FrameBytes;
            Info = new FrameNumberInfo(native.NumFrames, header);
        }
        catch
        {
            if (_preview != 0) _close?.Invoke(_preview);
            if (_context != 0) _delete?.Invoke(_context);
            if (_library != 0) NativeLibrary.Free(_library);
            if (_avisynth != 0) NativeLibrary.Free(_avisynth);
            throw;
        }
    }

    private Exception Error(string prefix) => new InvalidOperationException($"{prefix}: {Marshal.PtrToStringUTF8(_error(_context))}");
    private void Check(int position) { ObjectDisposedException.ThrowIf(_disposed, this); ArgumentOutOfRangeException.ThrowIfNegative(position); if (position >= Info.NumFrames) throw new ArgumentOutOfRangeException(nameof(position)); }
    public KfmFrameProperties? GetProperties(int position)
    {
        lock (_gate)
        {
            Check(position);
            int rc = _props(_preview, position, out int type, out int duration);
            if (rc < 0) throw Error("KFM属性を取得できませんでした");
            if (rc == 0) return null;
            if (type is < 1 or > 4 || duration is < 1 or > 4) throw new InvalidDataException("KFM属性が不正です。");
            return new(type, duration);
        }
    }
    public Y4mFrame GetFrame(int position)
    {
        lock (_gate)
        {
            Check(position);
            nint[] planes = new nint[3]; int[] pitches = new int[3];
            if (_frame(_preview, position, planes, pitches) == 0) throw Error("フレームを取得できませんでした");
            var h = Info.Header; var (cw, ch) = h.ChromaPlaneSize; int bps = h.BytesPerSample;
            int[] widths = [checked(h.Width * bps), checked(cw * bps), checked(cw * bps)];
            int[] heights = [h.Height, ch, ch];
            var data = new byte[h.FrameBytes]; int offset = 0;
            for (int i = 0; i < 3; i++)
            {
                if (planes[i] == 0 || Math.Abs((long)pitches[i]) < widths[i]) throw new InvalidDataException("YUVプレーンが不正です。");
                for (int y = 0; y < heights[i]; y++) { Marshal.Copy(planes[i] + checked(y * pitches[i]), data, offset, widths[i]); offset += widths[i]; }
            }
            return new Y4mFrame(h, h.Fps > 0 ? position / h.Fps : null, null, data);
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_preview != 0) { _close(_preview); _preview = 0; }
            if (_context != 0) { _delete(_context); _context = 0; }
            NativeLibrary.Free(_library);
            if (_avisynth != 0) NativeLibrary.Free(_avisynth);
        }
    }
}
