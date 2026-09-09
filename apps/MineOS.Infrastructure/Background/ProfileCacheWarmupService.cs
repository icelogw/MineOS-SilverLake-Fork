using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MineOS.Application.Interfaces;

namespace MineOS.Infrastructure.Background;

/// <summary>
/// Keeps the profile list warm so no operator ever waits for it.
///
/// The list is assembled from Mojang, PaperMC, Spigot's Jenkins and Microsoft,
/// and is cached in memory for ten minutes. Nothing kept it warm, so the cost of
/// building it landed on whichever request happened to arrive with a cold or
/// expired cache — which in practice meant someone opening a page that lists
/// profiles right after the container started, and waiting.
///
/// Refreshing on an interval shorter than the cache lifetime means the cache is
/// re-filled before it can expire, so the slow path is only ever taken here, in
/// the background, and never by a request.
/// </summary>
public sealed class ProfileCacheWarmupService : BackgroundService
{
    /// <summary>
    /// Comfortably inside ProfileService's ten-minute cache lifetime, so a
    /// refresh always lands before the entries it is replacing go stale.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(8);

    /// <summary>
    /// A short pause before the first warm-up. Startup is already busy bringing
    /// servers back up; this is not urgent enough to compete with that.
    /// </summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProfileCacheWarmupService> _logger;

    public ProfileCacheWarmupService(
        IServiceScopeFactory scopeFactory,
        ILogger<ProfileCacheWarmupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var succeeded = await WarmAsync(stoppingToken);

            try
            {
                // A failed warm-up is usually the network not being ready yet, so
                // retry sooner than the normal cadence rather than leaving the
                // cache cold for the next eight minutes.
                await Task.Delay(succeeded ? RefreshInterval : RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> WarmAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var profileService = scope.ServiceProvider.GetRequiredService<IProfileService>();

            var started = DateTimeOffset.UtcNow;
            var profiles = await profileService.ListProfilesAsync(cancellationToken);
            var elapsed = DateTimeOffset.UtcNow - started;

            _logger.LogInformation(
                "Profile cache warmed: {Count} profiles in {ElapsedMs:N0} ms",
                profiles.Count,
                elapsed.TotalMilliseconds);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never fatal. A cold cache costs a slow page load, not a broken one,
            // and the upstreams this depends on are outside our control.
            _logger.LogWarning(ex, "Profile cache warm-up failed; will retry");
            return false;
        }
    }
}
