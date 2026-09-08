using MineOS.Application.Dtos;
using MineOS.Infrastructure.Services;

namespace MineOS.Tests.Unit;

/// <summary>
/// The collector writes every 15 seconds and nothing prunes it, so a long window
/// is tens of thousands of rows. These cover the reduction that makes a week-long
/// chart the same cost to send and draw as an hour.
/// </summary>
public class PerformanceDownsampleTests
{
    private static List<PerformanceSampleDto> Samples(int count, Func<int, double> cpu, Func<int, double?>? tps = null)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count)
            .Select(i => new PerformanceSampleDto(
                "srv",
                start.AddSeconds(i * 15),
                true,
                cpu(i),
                100 + i,
                4096,
                tps?.Invoke(i),
                i % 5))
            .ToList();
    }

    [Fact]
    public void A_series_already_within_the_budget_is_returned_untouched()
    {
        var samples = Samples(50, i => i);

        var result = PerformanceService.Downsample(samples, 500);

        Assert.Same(samples, result);
    }

    [Fact]
    public void A_long_series_is_reduced_to_the_budget()
    {
        // A week at one sample per 15s.
        var samples = Samples(40_320, i => i % 100);

        var result = PerformanceService.Downsample(samples, 500);

        Assert.True(result.Count <= 500, $"Expected at most 500 points, got {result.Count}.");
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Every_sample_is_accounted_for()
    {
        // Ceiling division on the bucket size: with 10 samples and 3 buckets a
        // floor would leave the tail unbucketed and silently drop recent data.
        var samples = Samples(10, i => 1);

        var result = PerformanceService.Downsample(samples, 3);

        Assert.True(result.Count <= 3);
        Assert.Equal(samples[^1].Timestamp, result[^1].Timestamp);
    }

    [Fact]
    public void Buckets_average_rather_than_drop_samples()
    {
        // 0,100,0,100... — averaging gives 50; taking every Nth would give 0 or
        // 100 depending where it landed, hiding the spikes entirely.
        var samples = Samples(100, i => i % 2 == 0 ? 0 : 100);

        var result = PerformanceService.Downsample(samples, 10);

        Assert.All(result, point => Assert.Equal(50, point.CpuPercent, 1));
    }

    [Fact]
    public void A_spike_survives_reduction()
    {
        // The reason to look at a performance chart at all.
        var samples = Samples(1000, i => i == 500 ? 100 : 0);

        var result = PerformanceService.Downsample(samples, 100);

        Assert.Contains(result, point => point.CpuPercent > 0);
    }

    [Fact]
    public void Timestamps_stay_in_order_and_end_at_the_newest_sample()
    {
        var samples = Samples(5000, i => i % 50);

        var result = PerformanceService.Downsample(samples, 200);

        Assert.Equal(samples[^1].Timestamp, result[^1].Timestamp);
        for (var i = 1; i < result.Count; i++)
        {
            Assert.True(
                result[i].Timestamp > result[i - 1].Timestamp,
                "Downsampled points must stay in chronological order.");
        }
    }

    [Fact]
    public void A_bucket_with_no_tps_reading_stays_null()
    {
        // TPS is nullable, and a proxy never reports one. Averaging a bucket of
        // nulls to zero would render as "0 TPS", which reads as a dead server.
        var samples = Samples(100, i => 1, _ => null);

        var result = PerformanceService.Downsample(samples, 10);

        Assert.All(result, point => Assert.Null(point.Tps));
    }

    [Fact]
    public void Tps_averages_only_the_samples_that_reported_one()
    {
        // Half the samples report 20 TPS, half report nothing. The answer is 20,
        // not 10 — a missing reading is not a reading of zero.
        var samples = Samples(100, i => 1, i => i % 2 == 0 ? 20 : null);

        var result = PerformanceService.Downsample(samples, 10);

        Assert.All(result, point => Assert.Equal(20, point.Tps!.Value, 1));
    }
}
