using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class FrameNumberControllerTests
{
    private sealed class FakeSource : IFrameNumberSource
    {
        public FrameNumberInfo Info { get; } = new(80, new Y4mHeader { Width = 4, Height = 4, FpsNum = 60, FpsDen = 1 });
        public List<int> Positions { get; } = [];
        public bool Missing { get; set; }
        public bool Disposed { get; private set; }
        public KfmFrameProperties? GetProperties(int position)
        {
            if (Missing) return null;
            // 24pの2,3フレーム周期を60fps番号で表す。
            return new(3, (position % 5) switch { 0 => 2, 1 => 1, 2 => 3, 3 => 2, _ => 1 });
        }
        public Y4mFrame GetFrame(int position)
        {
            Positions.Add(position);
            return new(Info.Header, position / 60.0, null, new byte[Info.Header.FrameBytes]);
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task 局所積み上げは全編の残存位置と一致する()
    {
        var source = new FakeSource();
        await using var controller = new FrameNumberController(source);
        var expected = new HashSet<int>();
        for (int n = 0; n < source.Info.NumFrames; n += source.GetProperties(n)!.Value.Duration) expected.Add(n);
        for (int n = 0; n < source.Info.NumFrames; n++)
            Assert.Equal(expected.Contains(n), (await controller.GetFrameAsync(n)).IsKept);
        Assert.Equal(2, await controller.GetNeighborAsync(0, 1));
        Assert.Equal(2, await controller.GetNeighborAsync(5, -1));
        Assert.Null(await controller.GetNeighborAsync(0, -1));
    }

    private sealed class RecordedSource : IFrameNumberSource
    {
        public readonly (int Type, int Duration, bool Kept)[] Rows;
        public FrameNumberInfo Info { get; }
        public RecordedSource()
        {
            Rows = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Data", "kfm_realtime_p2.csv"))
                .Select(line => { var p = line.Split(','); return (int.Parse(p[0]), int.Parse(p[1]), p[2] == "1"); }).ToArray();
            Info = new(Rows.Length, new Y4mHeader { Width = 2, Height = 2, FpsNum = 60, FpsDen = 1 });
        }
        public KfmFrameProperties? GetProperties(int n) => new(Rows[n].Type, Rows[n].Duration);
        public Y4mFrame GetFrame(int n) => new(Info.Header, null, null, new byte[Info.Header.FrameBytes]);
        public void Dispose() { }
    }

    [Fact]
    public async Task 実測属性で位相合わせを検証する()
    {
        var source = new RecordedSource();
        await using var controller = new FrameNumberController(source);
        Assert.Equal(new[] { 1, 2, 3 }, source.Rows.Select(x => x.Type).Distinct().Order().ToArray());
        int withoutPrerollErrors = 0;
        for (int start = 20; start < source.Rows.Length; start += 10)
        {
            var local = new HashSet<int>();
            for (int n = start; n < Math.Min(start + 10, source.Rows.Length); n += source.Rows[n].Duration) local.Add(n);
            for (int n = start; n < Math.Min(start + 10, source.Rows.Length); n++)
            {
                if (local.Contains(n) != source.Rows[n].Kept) withoutPrerollErrors++;
                Assert.Equal(source.Rows[n].Kept, (await controller.GetFrameAsync(n)).IsKept);
            }
        }
        Assert.True(withoutPrerollErrors > 0);
    }

    [Fact]
    public async Task 属性がなければ全フレームを保持する()
    {
        var source = new FakeSource { Missing = true };
        await using var controller = new FrameNumberController(source);
        Assert.True((await controller.GetFrameAsync(7)).IsKept);
        Assert.Null((await controller.GetFrameAsync(7)).Kfm);
        Assert.Equal(8, await controller.GetNeighborAsync(7, 1));
    }

    [Fact]
    public async Task 明示先読みとキャッシュと事前取消の終了状態を返す()
    {
        var source = new FakeSource();
        await using (var controller = new FrameNumberController(source, source.Info.Header.FrameBytes * 3L))
        {
            await controller.PrefetchAsync(4, 1, 1);
            _ = await controller.GetFrameAsync(4);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.GetFrameAsync(5, cts.Token));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.GetFrameAsync(80));
        }
        Assert.True(source.Disposed);
        Assert.Equal(new[] { 4, 2, 5 }, source.Positions);
    }
}
