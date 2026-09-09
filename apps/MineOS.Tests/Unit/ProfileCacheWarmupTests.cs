using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;
using MineOS.Infrastructure.Background;

namespace MineOS.Tests.Unit;

/// <summary>
/// The profile list is built from four external services and cached for ten
/// minutes. Nothing kept it warm, so the cost of building it landed on whichever
/// request found the cache cold — ~18s for an operator opening a page that lists
/// profiles just after startup. These cover the service that moves that cost off
/// the request path.
/// </summary>
public class ProfileCacheWarmupTests
{
    private static (ProfileCacheWarmupService Service, Mock<IProfileService> Profiles) Build()
    {
        var profiles = new Mock<IProfileService>();
        profiles
            .Setup(p => p.ListProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProfileDto>());

        var services = new ServiceCollection();
        services.AddScoped(_ => profiles.Object);
        var provider = services.BuildServiceProvider();

        var service = new ProfileCacheWarmupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<ProfileCacheWarmupService>>());

        return (service, profiles);
    }

    [Fact]
    public async Task It_warms_the_cache_without_being_asked()
    {
        var (service, profiles) = Build();

        await service.StartAsync(CancellationToken.None);

        // The service waits briefly before its first pass so it does not compete
        // with the rest of startup.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                profiles.Verify(p => p.ListProfilesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
                break;
            }
            catch (MockException)
            {
                await Task.Delay(250);
            }
        }

        await service.StopAsync(CancellationToken.None);

        profiles.Verify(p => p.ListProfilesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task A_failing_upstream_does_not_bring_the_service_down()
    {
        // The upstreams are outside our control. A cold cache is a slow page,
        // not a broken application, so the loop must survive and retry.
        var profiles = new Mock<IProfileService>();
        profiles
            .Setup(p => p.ListProfilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("piston-meta unreachable"));

        var services = new ServiceCollection();
        services.AddScoped(_ => profiles.Object);
        var provider = services.BuildServiceProvider();

        var service = new ProfileCacheWarmupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<ProfileCacheWarmupService>>());

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(8));

        // Still running, and the exception did not escape.
        Assert.Null(service.ExecuteTask?.Exception);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_it_does_not_throw()
    {
        var (service, _) = Build();

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Null(service.ExecuteTask?.Exception);
    }
}
