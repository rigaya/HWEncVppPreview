using System.Globalization;
using System.Text;

namespace HWEncParamViewer.Core;

/// <summary>y4m のクロマ形式。</summary>
public enum Y4mChroma
{
    C420,
    C422,
    C444,
    /// <summary>輝度のみ (Cmono)。クロマプレーンは存在しない。</summary>
    Mono,
}

/// <summary>y4m のヘッダ (YUV4MPEG2 行) が壊れている、または未対応のとき。</summary>
public class Y4mFormatException : Exception
{
    public Y4mFormatException(string message) : base(message) { }
}

/// <summary>ストリームがフレームの途中で終わったとき。それまでに読めたフレームは有効。</summary>
public sealed class Y4mTruncatedException : Y4mFormatException
{
    /// <summary>切れたフレームの Xts (FRAME 行に無ければ null)。</summary>
    public double? Xts { get; }
    /// <summary>フレームデータとして期待したバイト数。</summary>
    public int ExpectedBytes { get; }
    /// <summary>実際に読めたバイト数。</summary>
    public int ActualBytes { get; }

    public Y4mTruncatedException(double? xts, int expectedBytes, int actualBytes)
        : base(CoreText.Y4mFrameTruncated(expectedBytes, actualBytes))
    {
        Xts = xts;
        ExpectedBytes = expectedBytes;
        ActualBytes = actualBytes;
    }
}

/// <summary>y4m ストリームヘッダ。</summary>
public sealed class Y4mHeader
{
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>フレームレートの分子 (F の左側)。F が無ければ 0。</summary>
    public int FpsNum { get; init; }
    public int FpsDen { get; init; }
    /// <summary>I タグ (p / t / b / m)。無ければ '?'。</summary>
    public char Interlace { get; init; } = '?';
    public int SarNum { get; init; }
    public int SarDen { get; init; }
    public Y4mChroma Chroma { get; init; } = Y4mChroma.C420;
    /// <summary>1サンプルあたりのビット数 (8 / 10 / 12 / 16 など)。</summary>
    public int BitDepth { get; init; } = 8;
    /// <summary>C タグの原文 (例: "420p10")。</summary>
    public string ColorSpaceTag { get; init; } = "420jpeg";
    /// <summary>X で始まる拡張タグ。キーは先頭の X を含む名前 (例: "XTIMEBASE")、値は = の右側 (= が無ければ空文字)。</summary>
    public IReadOnlyDictionary<string, string> ExtTags { get; init; } = new Dictionary<string, string>();

    /// <summary>出力の色範囲。未指定ならnull。原文の値は変換側で解釈する。</summary>
    public string? ColorRange => Extension("XCOLORRANGE");
    /// <summary>出力の色行列。未指定ならnull。</summary>
    public string? ColorMatrix => Extension("XCOLORMATRIX");
    /// <summary>出力の原色。未指定ならnull。</summary>
    public string? ColorPrimaries => Extension("XCOLORPRIM");
    /// <summary>出力の伝達特性。未指定ならnull。</summary>
    public string? Transfer => Extension("XTRANSFER");
    /// <summary>出力の色差の位置。未指定ならnull。</summary>
    public string? ChromaLocation => Extension("XCHROMALOC");

    private string? Extension(string name) => ExtTags.TryGetValue(name, out string? value) ? value : null;

    /// <summary>フレームレート (F が無い / 0 のときは 0)。</summary>
    public double Fps => FpsDen > 0 ? (double)FpsNum / FpsDen : 0.0;

    /// <summary>1サンプルのバイト数。8bit 超は 2 バイト (リトルエンディアン)。</summary>
    public int BytesPerSample => BitDepth > 8 ? 2 : 1;

    /// <summary>クロマプレーン1枚の幅と高さ。奇数サイズは切り上げ。</summary>
    public (int Width, int Height) ChromaPlaneSize => Chroma switch
    {
        Y4mChroma.C420 => ((Width + 1) / 2, (Height + 1) / 2),
        Y4mChroma.C422 => ((Width + 1) / 2, Height),
        Y4mChroma.C444 => (Width, Height),
        _ => (0, 0),
    };

    /// <summary>FRAME 1枚のデータ部のバイト数。</summary>
    public int FrameBytes
    {
        get
        {
            var (cw, ch) = ChromaPlaneSize;
            long samples = (long)Width * Height + (Chroma == Y4mChroma.Mono ? 0 : 2L * cw * ch);
            return checked((int)(samples * BytesPerSample));
        }
    }

    /// <summary>"YUV4MPEG2 ..." の1行 (改行なし) をパースする。</summary>
    public static Y4mHeader Parse(string line)
    {
        const string magic = "YUV4MPEG2";
        if (!line.StartsWith(magic, StringComparison.Ordinal))
        {
            throw new Y4mFormatException(CoreText.Y4mMagicInvalid(Truncate(line)));
        }
        int width = 0, height = 0, fpsN = 0, fpsD = 0, sarN = 0, sarD = 0;
        char interlace = '?';
        string csTag = "420jpeg";
        var ext = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var tok in line[magic.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var body = tok[1..];
            switch (tok[0])
            {
                case 'W': width = ParseInt(body, tok); break;
                case 'H': height = ParseInt(body, tok); break;
                case 'F': (fpsN, fpsD) = ParseRatio(body, tok); break;
                case 'A': (sarN, sarD) = ParseRatio(body, tok); break;
                case 'I': interlace = body.Length > 0 ? body[0] : '?'; break;
                case 'C': csTag = body; break;
                case 'X':
                    // 拡張タグは "XNAME=value" 形式。値に '=' や ':' を含みうるので最初の '=' だけで分ける
                    int eq = tok.IndexOf('=');
                    if (eq < 0) ext[tok] = string.Empty;
                    else ext[tok[..eq]] = tok[(eq + 1)..];
                    break;
                default: break; // 未知のタグは無視 (y4m 仕様どおり)
            }
        }
        if (width <= 0 || height <= 0)
        {
            throw new Y4mFormatException(CoreText.Y4mSizeInvalid(Truncate(line)));
        }
        var (chroma, depth) = ParseColorSpace(csTag);
        return new Y4mHeader
        {
            Width = width, Height = height, FpsNum = fpsN, FpsDen = fpsD,
            Interlace = interlace, SarNum = sarN, SarDen = sarD,
            Chroma = chroma, BitDepth = depth, ColorSpaceTag = csTag, ExtTags = ext,
        };
    }

    /// <summary>C タグ ("420jpeg" "420p10" "444" "mono" "mono12" など) をクロマ形式とビット深度に分ける。</summary>
    internal static (Y4mChroma, int) ParseColorSpace(string tag)
    {
        Y4mChroma chroma;
        string rest;
        if (tag.StartsWith("mono", StringComparison.Ordinal)) { chroma = Y4mChroma.Mono; rest = tag[4..]; }
        else if (tag.StartsWith("420", StringComparison.Ordinal)) { chroma = Y4mChroma.C420; rest = tag[3..]; }
        else if (tag.StartsWith("422", StringComparison.Ordinal)) { chroma = Y4mChroma.C422; rest = tag[3..]; }
        else if (tag.StartsWith("444", StringComparison.Ordinal)) { chroma = Y4mChroma.C444; rest = tag[3..]; }
        else throw new Y4mFormatException(CoreText.ColorSpaceUnsupported(tag));

        // 8bit 系の接尾辞 (420jpeg / 420mpeg2 / 420paldv) は 8bit。"p10" "p12" "p16" はビット深度。
        // mono は ffmpeg 流に "mono10" "mono12" のように p 無しで数字が付く。
        if (rest.Length == 0 || rest is "jpeg" or "mpeg2" or "paldv") return (chroma, 8);
        var digits = rest.StartsWith('p') ? rest[1..] : (chroma == Y4mChroma.Mono ? rest : "");
        if (digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int depth)
            && depth >= 8 && depth <= 16)
        {
            return (chroma, depth);
        }
        throw new Y4mFormatException(CoreText.ColorSpaceUnsupported(tag));
    }

    private static int ParseInt(string s, string tok) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v)
            ? v : throw new Y4mFormatException(CoreText.Y4mTagInvalid(tok));

    private static (int, int) ParseRatio(string s, string tok)
    {
        int colon = s.IndexOf(':');
        if (colon < 0) throw new Y4mFormatException(CoreText.Y4mTagInvalid(tok));
        return (ParseInt(s[..colon], tok), ParseInt(s[(colon + 1)..], tok));
    }

    private static string Truncate(string s) => s.Length <= 80 ? s : s[..80] + "...";
}

/// <summary>y4m の1フレーム。</summary>
public sealed class Y4mFrame
{
    public Y4mFrame(Y4mHeader header, double? xts, double? xdur, byte[] data)
    {
        Header = header;
        Xts = xts;
        Xdur = xdur;
        _legacyData = data;
    }

    public Y4mHeader Header { get; }
    /// <summary>FRAME 行の Xts (秒)。無ければ null。</summary>
    public double? Xts { get; }
    /// <summary>FRAME 行の Xdur (秒)。無ければ null。</summary>
    public double? Xdur { get; }
    /// <summary>Y、U、V の順に詰めたプレーンデータ。</summary>
    private readonly byte[]? _legacyData;
    internal RawFrameOwner? RawOwner
    {
        get;
    }
    internal long FrameId => RawOwner?.FrameId ?? 0;
    internal long RawRunId => RawOwner?.RawRunId ?? 0;
    internal long RawByteLength => RawOwner?.Bytes ?? _legacyData!.LongLength;
    internal byte[] RawPixels => RawOwner?.ReadBuffer() ?? _legacyData!;
    internal Y4mFrame(Y4mHeader header, double? xts, double? xdur, RawFrameOwner owner, bool owned)
    {
        Header = header;
        Xts = xts;
        Xdur = xdur;
        RawOwner = owner;
    }
    public byte[] Data => RawOwner == null ? _legacyData! : throw new InvalidOperationException("owned rawは公開Dataへ出せません。");
}

/// <summary>y4m を Stream から逐次読む。</summary>
/// <remarks>
/// 1バイトずつ読む行読みを使うので、パイプなど遅い Stream は呼び出し側で BufferedStream に包むこと。
/// 常駐サーバの stdout (PreviewProtocolReader) のように y4m の後ろに別の行が続く場合でも、
/// 必要なバイトしか消費しない。
/// </remarks>
public sealed class Y4mReader
{
    private static long _nextTraceReaderId;
    private readonly long _traceReaderId = PerfTrace.Enabled ? Interlocked.Increment(ref _nextTraceReaderId) : 0;
    private readonly Stream _stream;

    /// <summary>先頭のヘッダ行を読んで作る。ストリームが空なら Y4mFormatException。</summary>
    public Y4mReader(Stream stream)
    {
        _stream = stream;
        var line = ReadLine(stream) ?? throw new Y4mFormatException(CoreText.Y4mEmpty);
        Header = Y4mHeader.Parse(line);
    }

    /// <summary>ヘッダ行を読み済みの Stream 状態から作る (PreviewProtocolReader 用)。</summary>
    internal Y4mReader(Stream stream, Y4mHeader header)
    {
        _stream = stream;
        Header = header;
    }

    public Y4mHeader Header { get; }

    /// <summary>次のフレームを読む。きれいに終端 (次の FRAME 行の前で EOF) なら null。</summary>
    /// <exception cref="Y4mTruncatedException">フレームの途中で切れていた</exception>
    public Y4mFrame? ReadFrame()
    {
        var line = ReadLine(_stream);
        return line == null ? null : ReadFrameBody(line);
    }

    /// <summary>FRAME 行を読み済みとして、データ部を読む。</summary>
    internal Y4mFrame ReadFrameBody(string frameLine) => ReadFrameBodyCore(frameLine, null, 0).Frame;
    internal RawFrameLease? ReadOwnedFrame(RawOwnershipScope scope, long runId)
    {
        string? line = ReadLine(_stream);
        return line == null ? null : ReadFrameBodyCore(line, scope, runId).Lease;
    }
    internal RawFrameLease ReadOwnedFrameBody(string line, RawOwnershipScope scope, long runId) => ReadFrameBodyCore(line, scope, runId).Lease!;
    private (Y4mFrame Frame, RawFrameLease? Lease) ReadFrameBodyCore(string frameLine, RawOwnershipScope? scope, long runId)
    {
        bool trace = PerfTrace.Enabled;
        long parseStart = trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (frameLine != "FRAME" && !frameLine.StartsWith("FRAME ", StringComparison.Ordinal))
        {
            throw new Y4mFormatException(CoreText.Y4mFrameExpected(frameLine));
        }
        double? xts = null, xdur = null;
        foreach (var tok in frameLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            if (tok.StartsWith("Xts=", StringComparison.Ordinal)) xts = ParseDouble(tok[4..]);
            else if (tok.StartsWith("Xdur=", StringComparison.Ordinal)) xdur = ParseDouble(tok[5..]);
        }
        int size = Header.FrameBytes;
        double parseMs = trace ? System.Diagnostics.Stopwatch.GetElapsedTime(parseStart).TotalMilliseconds : 0;
        long allocationStart = trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var data = new byte[size];
        // 実Readの待機中から所有を記録する。取消要求は返却点ではない。
        RawFrameLease? owned = scope?.Create(Header, xts, xdur, data, runId, "Reader");
        try
        {
        double allocationMs = trace ? System.Diagnostics.Stopwatch.GetElapsedTime(allocationStart).TotalMilliseconds : 0;
        long readStart = trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        int got = 0, readCalls = 0;
        while (got < size)
        {
            int n = _stream.Read(data, got, size - got);
            if (trace) readCalls++;
            if (n <= 0) throw new Y4mTruncatedException(xts, size, got);
            got += n;
        }
        // 読取時間にはパイプやエンコーダの出力待ちも含み、CPU時間とは区別する。
        if (trace) PerfTrace.Write("y4m.frame", new { readerId = _traceReaderId, Xts = xts, Xdur = xdur,
            bytes = size, parseMs, allocationMs, readMs = System.Diagnostics.Stopwatch.GetElapsedTime(readStart).TotalMilliseconds, readCalls });
            return (owned?.Frame ?? new Y4mFrame(Header, xts, xdur, data), owned);
        }
        catch
        {
            owned?.Dispose();
            throw;
        }
    }

    private static double? ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    /// <summary>'\n' までを1行として読む (改行は含めない)。1バイトも読めずに EOF なら null。</summary>
    internal static string? ReadLine(Stream stream)
    {
        var buf = new List<byte>(64);
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0) break;
            if (b == '\n') return Encoding.ASCII.GetString(buf.ToArray());
            buf.Add((byte)b);
            // 壊れた入力 (改行が来ない) でメモリを食い尽くさないための上限。ヘッダ行は高々数百バイト。
            if (buf.Count > 4096) throw new Y4mFormatException(CoreText.Y4mLineTooLong);
        }
        // 改行なしで EOF: 中途半端な行は捨てて、データなしと同じ扱いにする
        if (buf.Count == 0) return null;
        throw new Y4mFormatException(CoreText.Y4mMissingNewline);
    }
}
