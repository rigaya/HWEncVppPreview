using System.Globalization;

namespace HWEncParamViewer.Core;

/// <summary>常駐サーバの stdout が仕様 (設計書 11.3) に合わないとき。</summary>
public sealed class PreviewProtocolException : Exception
{
    public PreviewProtocolException(string message) : base(message) { }
}

/// <summary>常駐サーバの stdout から読み取ったイベント。</summary>
public abstract record PreviewEvent;

/// <summary>RGYPREVIEW READY。key=value を辞書で保持する (例: version)。</summary>
public sealed record PreviewReady(IReadOnlyDictionary<string, string> Values) : PreviewEvent;

/// <summary>RGYPREVIEW BEGIN id=N。</summary>
public sealed record PreviewBegin(long Id) : PreviewEvent;

/// <summary>BEGIN の後の y4m ヘッダ。出力フレームが 0 枚の要求では発生しない。</summary>
public sealed record PreviewHeader(Y4mHeader Header) : PreviewEvent;

/// <summary>y4m の1フレーム。</summary>
public sealed record PreviewFrame(Y4mFrame Frame) : PreviewEvent;

/// <summary>RGYPREVIEW END id=N rc=R。rc は 0 が成功、それ以外は RGY_ERR の値 (負数もありうる)。</summary>
public sealed record PreviewEnd(long Id, int Rc) : PreviewEvent;

/// <summary>
/// 常駐サーバの stdout (READY → (BEGIN → y4m → END)*) を、イベントの列として返す。
/// </summary>
/// <remarks>
/// 内部で BufferedStream に包む。呼び出し側は、このクラスを作った後は元の Stream を直接読まないこと。
/// </remarks>
public sealed class PreviewProtocolReader
{
    private const string Prefix = "RGYPREVIEW ";
    private static long _nextTraceReaderId;
    private readonly long _traceReaderId = PerfTrace.Enabled ? Interlocked.Increment(ref _nextTraceReaderId) : 0;
    private long? _traceRequestId;

    private readonly Stream _stream;
    private bool _inRequest;
    private Y4mReader? _y4m;

    public PreviewProtocolReader(Stream stream)
    {
        _stream = new BufferedStream(stream, 1 << 16);
    }

    /// <summary>次のイベントを返す。ストリームの終端 (EOF) では null。</summary>
    /// <exception cref="PreviewProtocolException">仕様にない行が来た</exception>
    /// <exception cref="Y4mTruncatedException">フレームの途中でストリームが切れた (サーバが落ちた等)</exception>
    public PreviewEvent? ReadEvent()
    {
        bool trace = PerfTrace.Enabled;
        long eventStart = trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        PreviewEvent? result = null;
        bool completed = false;
        try
        {
            string? line = null;
            bool lineCompleted = false;
            long lineStart = trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                line = Y4mReader.ReadLine(_stream);
                lineCompleted = true;
            }
            finally
            {
                if (trace) PerfTrace.Write("protocol.line", new { readerId = _traceReaderId, requestId = _traceRequestId,
                    elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(lineStart).TotalMilliseconds,
                    characters = line?.Length, completed = lineCompleted });
            }
            if (line == null) { completed = true; return null; }

            if (line.StartsWith(Prefix, StringComparison.Ordinal))
            {
                result = ParseControlLine(line);
                if (trace && result is PreviewBegin begin) _traceRequestId = begin.Id;
                completed = true;
                return result;
            }
            if (!_inRequest)
            {
                throw new PreviewProtocolException(CoreText.ProtocolLineOutsideBegin(Short(line)));
            }
            if (line.StartsWith("YUV4MPEG2", StringComparison.Ordinal))
            {
                // 1要求のヘッダは1回だけとし、計測の有無で検証順や読取順を変えない。
                if (_y4m != null) throw new PreviewProtocolException(CoreText.DuplicateY4mHeader);
                long headerStart = trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                var header = Y4mHeader.Parse(line);
                if (trace) PerfTrace.Write("protocol.header", new { readerId = _traceReaderId, requestId = _traceRequestId,
                    elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(headerStart).TotalMilliseconds, header.Width, header.Height });
                _y4m = new Y4mReader(_stream, header);
                result = new PreviewHeader(header);
                completed = true;
                return result;
            }
            if (line == "FRAME" || line.StartsWith("FRAME ", StringComparison.Ordinal))
            {
                if (_y4m == null) throw new PreviewProtocolException(CoreText.FrameBeforeY4mHeader);
                result = new PreviewFrame(_y4m.ReadFrameBody(line));
                completed = true;
                return result;
            }
            throw new PreviewProtocolException(CoreText.ProtocolLineUnexpected(Short(line)));
        }
        finally
        {
            if (trace) PerfTrace.Write("protocol.event", new { readerId = _traceReaderId, requestId = _traceRequestId,
                kind = completed ? result?.GetType().Name ?? "EOF" : "Error", completed,
                elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(eventStart).TotalMilliseconds,
                Xts = result is PreviewFrame frame ? frame.Frame.Xts : (double?)null });
        }
    }

    private PreviewEvent ParseControlLine(string line)
    {
        var tokens = line[Prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw new PreviewProtocolException(CoreText.ProtocolControlUnknown(Short(line)));
        var kv = ParseKeyValues(tokens.Skip(1));

        switch (tokens[0])
        {
            case "READY":
                return new PreviewReady(kv);
            case "BEGIN":
                if (_inRequest) throw new PreviewProtocolException(CoreText.BeginBeforeEnd);
                _inRequest = true;
                _y4m = null;
                return new PreviewBegin(RequireLong(kv, "id", line));
            case "END":
                if (!_inRequest) throw new PreviewProtocolException(CoreText.EndBeforeBegin);
                _inRequest = false;
                _y4m = null;
                return new PreviewEnd(RequireLong(kv, "id", line), RequireInt(kv, "rc", line));
            default:
                throw new PreviewProtocolException(CoreText.ProtocolControlUnknown(Short(line)));
        }
    }

    private static Dictionary<string, string> ParseKeyValues(IEnumerable<string> tokens)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in tokens)
        {
            int eq = t.IndexOf('=');
            if (eq > 0) d[t[..eq]] = t[(eq + 1)..];
        }
        return d;
    }

    private static int RequireInt(Dictionary<string, string> kv, string key, string line) =>
        kv.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v)
            ? v : throw new PreviewProtocolException(CoreText.ProtocolNumberInvalid(key, Short(line)));

    // サーバの id は int64_t なので long で受ける (rc は RGY_ERR で int)
    private static long RequireLong(Dictionary<string, string> kv, string key, string line) =>
        kv.TryGetValue(key, out var s) && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)
            ? v : throw new PreviewProtocolException(CoreText.ProtocolNumberInvalid(key, Short(line)));

    private static string Short(string s) => s.Length <= 80 ? s : s[..80] + "...";
}
