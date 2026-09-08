using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;
using MineOS.Domain.Entities;
using MineOS.Infrastructure.Persistence;
using MineOS.Infrastructure.Services;

namespace MineOS.Tests.Unit;

/// <summary>
/// Covers the authority model behind the plugin API. These tokens are handed to
/// game servers running third-party code, so the tests that matter most are the
/// negative ones: what a token must NOT be able to do.
/// </summary>
public class PluginTokenServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Mock<IProxyForwardingService> _proxy = new();
    private readonly FixedTimeProvider _time = new() { Now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) };
    private readonly PluginTokenService _service;

    public PluginTokenServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options);
        _db.Database.Migrate();

        // Default: this server fronts nothing. Tests that care opt into backends.
        _proxy.Setup(p => p.GetProxyBackendsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) =>
                new ProxyBackendSummaryDto(name, Array.Empty<BackendForwardingDto>()));

        _service = new PluginTokenService(_db, _proxy.Object, _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<IssuedPluginTokenDto> IssueAsync(
        string server = "hub",
        bool proxyBackends = false,
        DateTimeOffset? expiresAt = null,
        params string[] scopes) =>
        _service.IssueAsync(
            new IssuePluginTokenRequest(
                "test token",
                server,
                scopes.Length > 0 ? scopes : new[] { PluginScopes.ServersRead },
                proxyBackends,
                expiresAt),
            createdByUserId: 1,
            CancellationToken.None);

    private void SetBackends(string proxy, params string[] backends) =>
        _proxy.Setup(p => p.GetProxyBackendsAsync(proxy, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxyBackendSummaryDto(
                proxy,
                backends.Select(b => new BackendForwardingDto(
                    b, "Secured", false, "VelocityModern", "Verified", proxy, "paper",
                    ServerOnlineMode: false, BackendForwardingConfigured: true, SecretMatches: true,
                    Exposure: "Isolated", ExposureDetail: null, RemediationAction: null)).ToList()));

    [Fact]
    public async Task Issued_secret_is_never_stored_in_plaintext()
    {
        var issued = await IssueAsync();

        var stored = await _db.PluginTokens.SingleAsync();

        Assert.StartsWith("mosp_", issued.Secret);
        Assert.NotEqual(issued.Secret, stored.TokenHash);
        Assert.DoesNotContain(issued.Secret, stored.TokenHash);
        // The stored prefix is a display aid, and must stay far too short to
        // meaningfully narrow a search of the remaining entropy.
        Assert.True(stored.TokenPrefix.Length < issued.Secret.Length / 2);
        Assert.StartsWith(stored.TokenPrefix, issued.Secret);
    }

    [Fact]
    public async Task Valid_token_authenticates_with_its_scopes()
    {
        var issued = await IssueAsync(scopes: new[] { PluginScopes.ServersRead, PluginScopes.ServersControl });

        var principal = await _service.AuthenticateAsync(issued.Secret, CancellationToken.None);

        Assert.NotNull(principal);
        Assert.Equal("hub", principal!.ServerName);
        Assert.True(principal.HasScope(PluginScopes.ServersControl));
        Assert.False(principal.HasScope(PluginScopes.ConsoleWrite));
    }

    [Fact]
    public async Task Unknown_secret_does_not_authenticate()
    {
        await IssueAsync();

        Assert.Null(await _service.AuthenticateAsync("mosp_not-a-real-token", CancellationToken.None));
        Assert.Null(await _service.AuthenticateAsync("", CancellationToken.None));
    }

    [Fact]
    public async Task Revoked_token_stops_authenticating()
    {
        var issued = await IssueAsync();

        Assert.True(await _service.RevokeAsync(issued.Token.Id, CancellationToken.None));

        Assert.Null(await _service.AuthenticateAsync(issued.Secret, CancellationToken.None));
    }

    [Fact]
    public async Task Expired_token_stops_authenticating()
    {
        // The panel's older ApiKey path carries an ExpiresAt column it never
        // checks. Plugin tokens must not repeat that.
        var issued = await IssueAsync(expiresAt: _time.Now.AddHours(1));

        Assert.NotNull(await _service.AuthenticateAsync(issued.Secret, CancellationToken.None));

        _time.Now = _time.Now.AddHours(2);

        Assert.Null(await _service.AuthenticateAsync(issued.Secret, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_scope_is_refused_at_issue_time()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            IssueAsync(scopes: new[] { "servers:reed" }));

        Assert.Contains("servers:reed", ex.Message);
    }

    [Fact]
    public async Task Token_can_always_act_on_its_own_server()
    {
        var issued = await IssueAsync(server: "hub");
        var principal = (await _service.AuthenticateAsync(issued.Secret, CancellationToken.None))!;

        Assert.True(await _service.CanActOnServerAsync(principal, "hub", CancellationToken.None));
        // Server names are matched case-insensitively, as elsewhere in the panel.
        Assert.True(await _service.CanActOnServerAsync(principal, "HUB", CancellationToken.None));
    }

    [Fact]
    public async Task Token_without_proxy_authority_cannot_touch_another_server()
    {
        SetBackends("hub", "survival", "creative");
        var issued = await IssueAsync(server: "hub", proxyBackends: false);
        var principal = (await _service.AuthenticateAsync(issued.Secret, CancellationToken.None))!;

        // Even though the proxy really does front these, the token was not
        // granted proxy authority, so its reach is itself and nothing else.
        Assert.False(await _service.CanActOnServerAsync(principal, "survival", CancellationToken.None));
        Assert.Single(await _service.ListServersInReachAsync(principal, CancellationToken.None));
    }

    [Fact]
    public async Task Proxy_token_can_act_on_its_own_backends_only()
    {
        SetBackends("hub", "survival", "creative");
        var issued = await IssueAsync(server: "hub", proxyBackends: true, scopes: PluginScopes.ServersControl);
        var principal = (await _service.AuthenticateAsync(issued.Secret, CancellationToken.None))!;

        Assert.True(await _service.CanActOnServerAsync(principal, "survival", CancellationToken.None));
        Assert.True(await _service.CanActOnServerAsync(principal, "creative", CancellationToken.None));

        // Not fronted by this proxy - a different network's server stays out of reach.
        Assert.False(await _service.CanActOnServerAsync(principal, "someone-elses-server", CancellationToken.None));
    }

    [Fact]
    public async Task Proxy_reach_follows_the_proxy_config_when_it_changes()
    {
        SetBackends("hub", "survival");
        var issued = await IssueAsync(server: "hub", proxyBackends: true);
        var principal = (await _service.AuthenticateAsync(issued.Secret, CancellationToken.None))!;

        Assert.True(await _service.CanActOnServerAsync(principal, "survival", CancellationToken.None));

        // Detaching the backend from the proxy revokes the plugin's authority over
        // it, with no second place for an operator to remember to update.
        SetBackends("hub");

        Assert.False(await _service.CanActOnServerAsync(principal, "survival", CancellationToken.None));
    }

    [Fact]
    public async Task Reach_lists_own_server_first_then_backends()
    {
        SetBackends("hub", "survival", "creative");
        var issued = await IssueAsync(server: "hub", proxyBackends: true);
        var principal = (await _service.AuthenticateAsync(issued.Secret, CancellationToken.None))!;

        var reach = await _service.ListServersInReachAsync(principal, CancellationToken.None);

        Assert.Equal(new[] { "hub", "survival", "creative" }, reach);
    }

    [Fact]
    public async Task Non_proxy_server_with_proxy_authority_reaches_only_itself()
    {
        // A token may be granted proxy authority before its server is set up as a
        // proxy. That is an empty reach, not a 500.
        _proxy.Setup(p => p.GetProxyBackendsAsync("survival", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("not a proxy"));

        var issued = await IssueAsync(server: "survival", proxyBackends: true);
        var principal = (await _service.AuthenticateAsync(issued.Secret, CancellationToken.None))!;

        Assert.Equal(new[] { "survival" }, await _service.ListServersInReachAsync(principal, CancellationToken.None));
    }

    [Fact]
    public async Task Event_is_filed_against_the_tokens_server_not_the_request_body()
    {
        var issued = await IssueAsync(server: "survival", scopes: PluginScopes.EventsWrite);
        var principal = (await _service.AuthenticateAsync(issued.Secret, CancellationToken.None))!;

        await _service.RecordEventAsync(
            principal,
            new PluginEventRequest("player.join", PlayerName: "steve"),
            CancellationToken.None);

        var stored = await _db.PluginEvents.SingleAsync();
        Assert.Equal("survival", stored.ServerName);
        Assert.Equal("player.join", stored.Type);
        Assert.Equal(issued.Token.Id, stored.TokenId);
        Assert.Equal(_time.Now, stored.ReceivedAt);
    }

    [Fact]
    public async Task Listing_tokens_never_exposes_a_secret()
    {
        var issued = await IssueAsync();

        var listed = await _service.ListAsync(null, CancellationToken.None);

        var dto = Assert.Single(listed);
        Assert.Equal(issued.Token.Id, dto.Id);
        // PluginTokenDto has no field that could carry the secret; assert the
        // prefix is all that surfaces.
        Assert.Equal(issued.Token.TokenPrefix, dto.TokenPrefix);
        Assert.NotEqual(issued.Secret, dto.TokenPrefix);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
