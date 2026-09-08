using MineOS.Application.Dtos;

namespace MineOS.Application.Interfaces;

public interface IPerformanceService
{
    Task<PerformanceSampleDto> GetRealtimeAsync(string serverName, CancellationToken cancellationToken);
    /// <summary>
    /// Samples across a window, oldest first, reduced to at most
    /// <paramref name="maxPoints"/> entries.
    ///
    /// The collector writes every 15 seconds and nothing prunes it, so a week is
    /// ~40,000 rows for one server. Returning them all would be a slow response
    /// and a chart no one can read; averaging into buckets keeps a long window
    /// the same size on the wire as a short one.
    /// </summary>
    Task<IReadOnlyList<PerformanceSampleDto>> GetHistoryAsync(
        string serverName,
        TimeSpan window,
        CancellationToken cancellationToken,
        int maxPoints = 500);
    Task RecordSampleAsync(string serverName, CancellationToken cancellationToken);
    IAsyncEnumerable<PerformanceSampleDto> StreamRealtimeAsync(
        string serverName,
        TimeSpan interval,
        CancellationToken cancellationToken);
    Task<SparkStatusDto> GetSparkStatusAsync(string serverName, CancellationToken cancellationToken);
}
