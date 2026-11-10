using System.Text;

namespace HWEncVppPreview.Tests;

internal static class TestUtil
{
    /// <summary>テスト用の y4m ヘッダ行 (改行付き) を作る。</summary>
    public static string Header(int w, int h, string c, string extra = "") =>
        $"YUV4MPEG2 W{w} H{h} F30:1 Ip A1:1 {extra}C{c}\n";

    /// <summary>FRAME 行 + データ。データは先頭バイトに連番を入れる。</summary>
    public static byte[] Frame(int dataBytes, string frameLine = "FRAME", byte fill = 0)
    {
        var head = Encoding.ASCII.GetBytes(frameLine + "\n");
        var buf = new byte[head.Length + dataBytes];
        head.CopyTo(buf, 0);
        Array.Fill(buf, fill, head.Length, dataBytes);
        return buf;
    }

    public static byte[] Concat(params object[] parts)
    {
        using var ms = new MemoryStream();
        foreach (var p in parts)
        {
            var b = p is string s ? Encoding.ASCII.GetBytes(s) : (byte[])p;
            ms.Write(b);
        }
        return ms.ToArray();
    }

    /// <summary>1バイトずつしか返さない Stream。パイプの細切れ到着を模す。</summary>
    public sealed class TrickleStream(byte[] data) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count == 0) return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
