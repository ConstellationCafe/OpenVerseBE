namespace OpenVerseBE.Tests;

public class DownloadQueueTests
{
    DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    DownloadQueue Queue(int maxActive = 2, int idleSeconds = 120) =>
        new(new BeOptions { AssetMaxActive = maxActive, AssetIdle = TimeSpan.FromSeconds(idleSeconds) }, () => _now);

    [Fact]
    public void Two_download_at_once_and_the_rest_line_up_in_order()
    {
        var q = Queue();
        Assert.True(q.Enter("a").Go);
        Assert.True(q.Enter("b").Go);
        Assert.Equal(1, q.Enter("c").Position);
        Assert.Equal(2, q.Enter("d").Position);
        // asking again keeps the place, it does not move you to the back
        Assert.Equal(1, q.Enter("c").Position);
        Assert.True(q.Enter("a").Go);
        Assert.Equal((2, 2), q.Counts());
    }

    [Fact]
    public void A_free_place_goes_to_the_first_in_line_not_to_a_newcomer()
    {
        var q = Queue(idleSeconds: 60);
        q.Enter("a"); q.Enter("b");
        q.Enter("c"); q.Enter("d");

        // a and b finish (stop asking); c and d keep asking
        _now += TimeSpan.FromSeconds(40);
        q.Enter("c"); q.Enter("d");
        _now += TimeSpan.FromSeconds(30);

        var newcomer = q.Enter("e");
        Assert.False(newcomer.Go);
        Assert.Equal(3, newcomer.Position);
        Assert.True(q.Enter("c").Go);
        Assert.True(q.Enter("d").Go);
        Assert.Equal(1, q.Enter("e").Position);
    }

    [Fact]
    public void Someone_who_stops_asking_drops_out_of_line()
    {
        var q = Queue(maxActive: 1, idleSeconds: 60);
        q.Enter("a");
        q.Enter("b");
        q.Enter("c");
        _now += TimeSpan.FromSeconds(50);
        q.Enter("a"); q.Enter("c");       // b went away
        _now += TimeSpan.FromSeconds(20);
        Assert.Equal(1, q.Enter("c").Position);
    }

    [Fact]
    public void A_long_transfer_keeps_its_place()
    {
        var q = Queue(maxActive: 1, idleSeconds: 60);
        q.Enter("a");
        for (var i = 0; i < 5; i++)
        {
            _now += TimeSpan.FromSeconds(30);
            q.Touch("a");
            q.Enter("b");
        }
        Assert.False(q.Enter("b").Go);
        Assert.True(q.Enter("a").Go);
    }

    [Fact]
    public void Zero_means_no_line()
    {
        var q = Queue(maxActive: 0);
        for (var i = 0; i < 10; i++) Assert.True(q.Enter("p" + i).Go);
    }

    [Fact]
    public void The_budget_is_shared_and_never_exceeded()
    {
        var rate = new DownloadRate(new BeOptions { AssetRateMbps = 8 });   // 1,000,000 bytes per second
        var t = _now;
        var total = TimeSpan.Zero;
        // two streams taking turns with 64 KiB chunks, all asked for at the same instant
        for (var i = 0; i < 30; i++) total = rate.Reserve(65536, t);
        // 29 chunks are booked ahead of the last one: 29 * 65536 bytes at 1 MB/s
        Assert.Equal(29 * 65536 / 1_000_000.0, total.TotalSeconds, 3);
        Assert.True(new DownloadRate(new BeOptions { AssetRateMbps = 0 }).Unlimited);
    }

    [Fact]
    public void Settings_come_from_the_environment()
    {
        var o = BeOptions.From(k => k switch
        {
            "OVBE_ASSET_RATE_MBPS" => "150.5",
            "OVBE_ASSET_MAX_ACTIVE" => "4",
            "OVBE_ASSET_IDLE_SECONDS" => "30",
            _ => null,
        });
        Assert.Equal(150.5, o.AssetRateMbps);
        Assert.Equal(4, o.AssetMaxActive);
        Assert.Equal(TimeSpan.FromSeconds(30), o.AssetIdle);

        var d = BeOptions.From(_ => null);
        Assert.Equal(64, d.AssetRateMbps);
        Assert.Equal(2, d.AssetMaxActive);
        Assert.Throws<InvalidOperationException>(() => BeOptions.From(k => k == "OVBE_ASSET_MAX_ACTIVE" ? "two" : null));
    }
}
