using System.Diagnostics;
using System.Text;

namespace HWEncParamViewer.Core;

internal static class ProcessSupport
{
    public static Process Start(string path, IEnumerable<string> arguments, Action<ProcessStartInfo>? configure)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        configure?.Invoke(info);
        return Process.Start(info) ?? throw new IOException("エンコーダを起動できませんでした");
    }

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }

    public static async Task ReapAsync(Process process)
    {
        Kill(process);
        await process.WaitForExitAsync().ConfigureAwait(false);
    }
}

/// <summary>パーサの読み取りより下で、実際のパイプ受信を監視する。</summary>
internal sealed class ProgressReadStream(Stream inner, Action onOutput) : Stream
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        if (read > 0) onOutput();
        return read;
    }
    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        if (read > 0) onOutput();
        return read;
    }
    public override int ReadByte()
    {
        int value = inner.ReadByte();
        if (value >= 0) onOutput();
        return value;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
