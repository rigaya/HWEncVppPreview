using HWEncParamViewer.Core;
using static HWEncParamViewer.Tests.TestUtil;

namespace HWEncParamViewer.Tests;

public class PreviewProtocolReaderTests
{
    private static List<PreviewEvent> ReadAll(byte[] data, bool trickle = false)
    {
        Stream s = trickle ? new TrickleStream(data) : new MemoryStream(data);
        var r = new PreviewProtocolReader(s);
        var list = new List<PreviewEvent>();
        PreviewEvent? e;
        while ((e = r.ReadEvent()) != null) list.Add(e);
        return list;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void READYからENDまでの一連(bool trickle)
    {
        var data = Concat(
            "RGYPREVIEW READY version=1 enc=qsv\n",
            "RGYPREVIEW BEGIN id=12\n",
            Header(2, 2, "420jpeg", "XTIMEBASE=1:90000 "),
            Frame(6, "FRAME Xts=0.000000 Xdur=0.033367", 1),
            Frame(6, "FRAME Xts=0.033367 Xdur=0.033367", 2),
            "RGYPREVIEW END id=12 rc=0\n");
        var ev = ReadAll(data, trickle);

        Assert.Equal(6, ev.Count);
        var ready = Assert.IsType<PreviewReady>(ev[0]);
        Assert.Equal("1", ready.Values["version"]);
        Assert.Equal("qsv", ready.Values["enc"]);
        Assert.Equal(12, Assert.IsType<PreviewBegin>(ev[1]).Id);
        Assert.Equal("1:90000", Assert.IsType<PreviewHeader>(ev[2]).Header.ExtTags["XTIMEBASE"]);
        Assert.Equal(1, Assert.IsType<PreviewFrame>(ev[3]).Frame.Data[0]);
        Assert.Equal(0.033367, Assert.IsType<PreviewFrame>(ev[4]).Frame.Xts);
        var end = Assert.IsType<PreviewEnd>(ev[5]);
        Assert.Equal((12, 0), (end.Id, end.Rc));
    }

    [Fact]
    public void フレームデータが改行や制御行そのもののバイト列でも壊れない()
    {
        // データ部に "RGYPREVIEW END" 相当の文字列があっても、長さで読むので影響しない
        var payload = System.Text.Encoding.ASCII.GetBytes("RGYPREVIEW END id=1 rc=0\n");
        var frame = Concat("FRAME Xts=0\n", payload[..6]);
        var data = Concat("RGYPREVIEW BEGIN id=1\n", Header(2, 2, "420jpeg"), frame, "RGYPREVIEW END id=1 rc=0\n");
        var ev = ReadAll(data);
        Assert.Equal(4, ev.Count);
        Assert.IsType<PreviewEnd>(ev[3]);
    }

    [Fact]
    public void ゼロフレームはBEGINの直後にEND()
    {
        var ev = ReadAll(Concat("RGYPREVIEW READY version=1\n", "RGYPREVIEW BEGIN id=3\n", "RGYPREVIEW END id=3 rc=0\n"));
        Assert.Equal(3, ev.Count);
        Assert.IsType<PreviewBegin>(ev[1]);
        Assert.Equal(0, Assert.IsType<PreviewEnd>(ev[2]).Rc);
    }

    [Fact]
    public void 複数の要求が連続する_ヘッダは要求ごと()
    {
        var data = Concat(
            "RGYPREVIEW READY version=1\n",
            "RGYPREVIEW BEGIN id=1\n", Header(2, 2, "420jpeg"), Frame(6, "FRAME Xts=0"), "RGYPREVIEW END id=1 rc=0\n",
            "RGYPREVIEW BEGIN id=2\n", "RGYPREVIEW END id=2 rc=0\n",
            "RGYPREVIEW BEGIN id=3\n", Header(4, 2, "444"), Frame(24, "FRAME Xts=0"), "RGYPREVIEW END id=3 rc=0\n");
        var ev = ReadAll(data);
        Assert.Equal(11, ev.Count);
        Assert.Equal(2, ev.OfType<PreviewHeader>().Count());
        Assert.Equal(new long[] { 1, 2, 3 }, ev.OfType<PreviewEnd>().Select(e => e.Id));
        Assert.Equal(24, ev.OfType<PreviewFrame>().Last().Frame.Data.Length);
    }

    [Fact]
    public void idはintの範囲を超えても読める()
    {
        var ev = ReadAll(Concat("RGYPREVIEW BEGIN id=9999999999\n", "RGYPREVIEW END id=9999999999 rc=0\n"));
        Assert.Equal(9999999999L, Assert.IsType<PreviewBegin>(ev[0]).Id);
        Assert.Equal(9999999999L, Assert.IsType<PreviewEnd>(ev[1]).Id);
    }

    [Fact]
    public void ENDのrcが負でも読める()
    {
        var ev = ReadAll(Concat("RGYPREVIEW BEGIN id=5\n", "RGYPREVIEW END id=5 rc=-5\n"));
        Assert.Equal(-5, Assert.IsType<PreviewEnd>(ev[1]).Rc);
    }

    [Fact]
    public void 中断された要求_フレームの後にrcが非0のEND()
    {
        var data = Concat("RGYPREVIEW BEGIN id=1\n", Header(2, 2, "420jpeg"), Frame(6, "FRAME Xts=0"),
            "RGYPREVIEW END id=1 rc=-27\n");
        var ev = ReadAll(data);
        Assert.Equal(-27, Assert.IsType<PreviewEnd>(ev[^1]).Rc);
    }

    [Fact]
    public void フレーム途中でEOFならTruncated()
    {
        var data = Concat("RGYPREVIEW BEGIN id=1\n", Header(2, 2, "420jpeg"), Frame(6, "FRAME Xts=0")[..^1]);
        var r = new PreviewProtocolReader(new MemoryStream(data));
        r.ReadEvent();
        r.ReadEvent();
        Assert.Throws<Y4mTruncatedException>(() => r.ReadEvent());
    }

    [Fact]
    public void END無しのEOFはnullで終わる()
    {
        var ev = ReadAll(Concat("RGYPREVIEW BEGIN id=1\n"));
        Assert.Single(ev);
    }

    [Theory]
    [InlineData("GARBAGE\n")]                                  // BEGIN の外
    [InlineData("RGYPREVIEW END id=1 rc=0\n")]                 // BEGIN なしの END
    [InlineData("RGYPREVIEW BEGIN id=1\nRGYPREVIEW BEGIN id=2\n")] // END なしの BEGIN
    [InlineData("RGYPREVIEW BEGIN\n")]                         // id なし
    [InlineData("RGYPREVIEW BEGIN id=1\nRGYPREVIEW END id=1\n")] // rc なし
    [InlineData("RGYPREVIEW BEGIN id=1\nRGYPREVIEW END id=x rc=0\n")]
    [InlineData("RGYPREVIEW BEGIN id=1\nFRAME\n")]             // ヘッダ前の FRAME
    [InlineData("RGYPREVIEW BEGIN id=1\nHELLO\n")]
    [InlineData("RGYPREVIEW FOO\n")]
    public void 仕様違反はPreviewProtocolException(string text)
    {
        Assert.Throws<PreviewProtocolException>(() => ReadAll(Concat(text)));
    }
}
