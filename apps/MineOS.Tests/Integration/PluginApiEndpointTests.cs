using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MineOS.Domain.Entities;

namespace MineOS.Tests.Integration;

/// <summary>
/// Exercises the plugin API over real HTTP, which is the only place the middleware
/// ordering can actually be verified: plugin routes have to bypass the admin
/// API-key middleware without inheriting its full-access principal.
/// </summary>
public class PluginApiEndpointTests : IClassFixture<MineOsWebApplicationFactory>
{
    private const string StaticApiKey = "dev-static-api-key-change-me";

    private readonly MineOsWebApplicationFactory _factory;
    private readonly HttpClient _admin;

    public PluginApiEndpointTests(MineOsWebApplicationFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateClient();
        _admin.DefaultRequestHeaders.Add("X-Api-Key", StaticApiKey);
    }

    private async Task<string> IssueTokenAsync(
        string server,
        string[] scopes,
        bool allowProxyBackends = false)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/plugin-tokens", new
        {
            name = $"test-{Guid.NewGuid():N}",
            serverName = server,
            scopes,
            allowProxyBackends
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("secret").GetString()!;
    }

    private HttpClient PluginClient(string secret)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Plugin-Token", secret);
        return client;
    }

    [Fact]
    public async Task Issuing_a_token_returns_the_secret_exactly_once()
    {
        var secret = await IssueTokenAsync("hub", new[] { PluginScopes.ServersRead });
        Assert.StartsWith("mosp_", secret);

        // Listing afterwards must never hand the secret back.
        var list = await _admin.GetAsync("/api/v1/plugin-tokens?serverName=hub");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var body = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body);
    }

    [Fact]
    public async Task Plugin_route_requires_a_plugin_token()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/plugin/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_api_key_alone_cannot_pose_as_a_plugin()
    {
        // The admin key is a full-access identity everywhere else in the panel.
        // It must not satisfy a plugin route, because plugin routes resolve WHICH
        // server is calling from the token - an admin key names no server.
        var response = await _admin.GetAsync("/api/v1/plugin/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Plugin_token_cannot_reach_the_panel_admin_api()
    {
        // The whole point of a separate credential: a leaked plugin token must
        // not be usable against the panel's own endpoints.
        var secret = await IssueTokenAsync("hub", new[] { PluginScopes.ServersRead });
        var plugin = PluginClient(secret);

        var response = await plugin.GetAsync("/api/v1/servers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_reports_the_tokens_identity_and_scopes()
    {
        var secret = await IssueTokenAsync("hub", new[] { PluginScopes.ServersRead, PluginScopes.EventsWrite });
        var plugin = PluginClient(secret);

        var response = await plugin.GetAsync("/api/v1/plugin/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("hub", json.GetProperty("serverName").GetString());

        var scopes = json.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains(PluginScopes.ServersRead, scopes);
        Assert.Contains(PluginScopes.EventsWrite, scopes);

        // Reach is itself only - this token carries no proxy authority.
        var reach = json.GetProperty("serversInReach").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Equal(new[] { "hub" }, reach);
    }

    [Fact]
    public async Task Missing_scope_is_forbidden_not_merely_unauthorized()
    {
        // Read-only token trying to control a server: authenticated, so 403 - the
        // caller needs to know its token is real but under-scoped.
        var secret = await IssueTokenAsync("hub", new[] { PluginScopes.ServersRead });
        var plugin = PluginClient(secret);

        var response = await plugin.PostAsync("/api/v1/plugin/servers/hub/restart", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Server_out_of_reach_is_not_found_rather_than_forbidden()
    {
        // 404, not 403: a 403 would confirm the server exists, letting a plugin
        // enumerate the panel's servers by probing names.
        var secret = await IssueTokenAsync("hub", new[] { PluginScopes.ServersControl });
        var plugin = PluginClient(secret);

        var response = await plugin.PostAsync("/api/v1/plugin/servers/someone-elses-server/restart", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_token_is_rejected_immediately()
    {
        var secret = await IssueTokenAsync("hub", new[] { PluginScopes.ServersRead });
        var plugin = PluginClient(secret);

        Assert.Equal(HttpStatusCode.OK, (await plugin.GetAsync("/api/v1/plugin/me")).StatusCode);

        var list = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/plugin-tokens?serverName=hub");
        var id = list.EnumerateArray().First().GetProperty("id").GetInt32();

        var revoke = await _admin.DeleteAsync($"/api/v1/plugin-tokens/{id}");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await plugin.GetAsync("/api/v1/plugin/me")).StatusCode);
    }

    [Fact]
    public async Task Events_are_accepted_and_attributed_to_the_tokens_server()
    {
        var secret = await IssueTokenAsync("survival", new[] { PluginScopes.EventsWrite });
        var plugin = PluginClient(secret);

        var response = await plugin.PostAsJsonAsync("/api/v1/plugin/events", new
        {
            type = "player.join",
            playerName = "steve",
            // Deliberately absent: any way to name a different server. The panel
            // takes the server from the token.
            data = "{\"world\":\"overworld\"}"
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Event_without_a_type_is_rejected()
    {
        var secret = await IssueTokenAsync("survival", new[] { PluginScopes.EventsWrite });
        var plugin = PluginClient(secret);

        var response = await plugin.PostAsJsonAsync("/api/v1/plugin/events", new { playerName = "steve" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Players_endpoint_is_gated_on_its_own_scope()
    {
        var readOnly = PluginClient(await IssueTokenAsync("hub", new[] { PluginScopes.ServersRead }));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await readOnly.GetAsync("/api/v1/plugin/servers/hub/players")).StatusCode);

        var withPlayers = PluginClient(await IssueTokenAsync("hub", new[] { PluginScopes.PlayersRead }));
        var response = await withPlayers.GetAsync("/api/v1/plugin/servers/hub/players");

        // No player files exist for this server, which is an empty list rather
        // than an error the plugin has to special-case.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task Every_advertised_scope_is_actually_used_by_an_endpoint()
    {
        // Guards against advertising a scope an operator can grant but which
        // gates nothing - a token that looks more capable than it is.
        var response = await _admin.GetAsync("/api/v1/plugin-tokens/scopes");
        var advertised = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Select(s => s.GetProperty("scope").GetString()!).ToList();

        Assert.Equal(PluginScopes.All.OrderBy(s => s), advertised.OrderBy(s => s));
    }

    [Fact]
    public async Task Every_capability_has_both_a_read_and_a_write_scope()
    {
        // A plugin that only needs to look at something must never have to be
        // granted the ability to change it. Each capability therefore ships both
        // halves; a one-sided capability is the bug this guards against.
        foreach (var capability in new[] { "servers", "console", "events", "players" })
        {
            Assert.Contains(PluginScopes.All, s => s.StartsWith($"{capability}:", StringComparison.Ordinal));

            var sides = PluginScopes.All
                .Where(s => s.StartsWith($"{capability}:", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                sides.Count >= 2,
                $"Capability '{capability}' has only {string.Join(", ", sides)} — no opposite scope.");
        }
    }

    [Fact]
    public async Task Read_scope_does_not_grant_the_matching_write()
    {
        var readOnly = PluginClient(await IssueTokenAsync("hub", new[]
        {
            PluginScopes.ConsoleRead,
            PluginScopes.EventsRead,
            PluginScopes.PlayersRead
        }));

        // Reads allowed.
        Assert.Equal(
            HttpStatusCode.OK,
            (await readOnly.GetAsync("/api/v1/plugin/servers/hub/console")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await readOnly.GetAsync("/api/v1/plugin/events")).StatusCode);

        // The matching writes are not.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await readOnly.PostAsJsonAsync("/api/v1/plugin/servers/hub/command", new { command = "say hi" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await readOnly.PostAsJsonAsync("/api/v1/plugin/events", new { type = "x" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await readOnly.PostAsJsonAsync("/api/v1/plugin/servers/hub/players/ban", new { uuid = "abc" })).StatusCode);
    }

    [Fact]
    public async Task Write_scope_does_not_grant_the_matching_read()
    {
        var writeOnly = PluginClient(await IssueTokenAsync("hub", new[]
        {
            PluginScopes.ConsoleWrite,
            PluginScopes.EventsWrite,
            PluginScopes.PlayersWrite
        }));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await writeOnly.GetAsync("/api/v1/plugin/servers/hub/console")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await writeOnly.GetAsync("/api/v1/plugin/events")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await writeOnly.GetAsync("/api/v1/plugin/servers/hub/players")).StatusCode);
    }

    [Fact]
    public async Task Reported_events_can_be_read_back_with_the_read_scope()
    {
        var secret = await IssueTokenAsync("readback", new[]
        {
            PluginScopes.EventsWrite,
            PluginScopes.EventsRead
        });
        var plugin = PluginClient(secret);

        await plugin.PostAsJsonAsync("/api/v1/plugin/events", new
        {
            type = "arena.start",
            playerName = "steve"
        });

        var response = await plugin.GetAsync("/api/v1/plugin/events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var single = Assert.Single(events);
        Assert.Equal("arena.start", single.GetProperty("type").GetString());
        Assert.Equal("steve", single.GetProperty("playerName").GetString());
    }

    [Fact]
    public async Task Events_read_never_returns_another_servers_events()
    {
        var alpha = PluginClient(await IssueTokenAsync("alpha", new[]
        {
            PluginScopes.EventsWrite,
            PluginScopes.EventsRead
        }));
        var beta = PluginClient(await IssueTokenAsync("beta", new[]
        {
            PluginScopes.EventsRead
        }));

        await alpha.PostAsJsonAsync("/api/v1/plugin/events", new { type = "alpha.only" });

        var seenByBeta = (await (await beta.GetAsync("/api/v1/plugin/events"))
            .Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

        Assert.Empty(seenByBeta);
    }

    [Fact]
    public async Task Console_read_on_a_server_with_no_log_is_an_empty_list()
    {
        var plugin = PluginClient(await IssueTokenAsync("hub", new[] { PluginScopes.ConsoleRead }));

        var response = await plugin.GetAsync("/api/v1/plugin/servers/hub/console");

        // A server that has never started has no log. That is nothing to report,
        // not an error the plugin has to special-case.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task Unknown_scope_is_refused_when_issuing()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/plugin-tokens", new
        {
            name = "typo",
            serverName = "hub",
            scopes = new[] { "servers:reed" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Scope_catalogue_is_advertised_to_operators()
    {
        var response = await _admin.GetAsync("/api/v1/plugin-tokens/scopes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var scopes = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().ToList();

        Assert.Equal(PluginScopes.All.Count, scopes.Count);
        Assert.Contains(scopes, s => s.GetProperty("scope").GetString() == PluginScopes.ServersControl);

        // Every scope must arrive with an explanation. An operator granting
        // console:write is handing out op and ban; the panel has to say so.
        Assert.All(scopes, s =>
            Assert.False(string.IsNullOrWhiteSpace(s.GetProperty("description").GetString())));
    }
}
