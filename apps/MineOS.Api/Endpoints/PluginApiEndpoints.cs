using Microsoft.AspNetCore.Mvc;
using MineOS.Api.Authorization;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;
using MineOS.Domain.Entities;

namespace MineOS.Api.Endpoints;

/// <summary>
/// The surface Minecraft plugins call, as opposed to <see cref="PluginEndpoints"/>,
/// which is the panel managing plugin JARs on disk.
///
/// Everything here authenticates with an X-Plugin-Token scoped to a single server.
/// The driving case is a proxy plugin restarting one of its own backends, so the
/// server-targeted routes resolve authority through the proxy's backend list
/// rather than through panel-wide roles.
/// </summary>
public static class PluginApiEndpoints
{
    public static IEndpointRouteBuilder MapPluginApiEndpoints(this IEndpointRouteBuilder app)
    {
        var plugin = app.MapGroup("/plugin").WithTags("Plugin API");

        plugin.MapGet("/me", async (
                HttpContext http,
                [FromServices] IPluginTokenService tokens,
                CancellationToken cancellationToken) =>
            {
                var principal = http.RequirePluginPrincipal();
                var reach = await tokens.ListServersInReachAsync(principal, cancellationToken);

                return Results.Ok(new PluginIdentityDto(
                    principal.Name,
                    principal.ServerName,
                    principal.Scopes.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                    principal.AllowProxyBackends,
                    reach));
            })
            .RequirePluginToken()
            .WithName("GetPluginIdentity")
            .WithSummary("What this token is and what it may reach");

        plugin.MapGet("/servers", async (
                HttpContext http,
                [FromServices] IPluginTokenService tokens,
                [FromServices] IServerService servers,
                CancellationToken cancellationToken) =>
            {
                var principal = http.RequirePluginPrincipal();
                var reach = await tokens.ListServersInReachAsync(principal, cancellationToken);

                var results = new List<PluginServerDto>(reach.Count);
                foreach (var name in reach)
                {
                    results.Add(await DescribeServerAsync(servers, name, principal, cancellationToken));
                }

                return Results.Ok(results);
            })
            .RequirePluginScope(PluginScopes.ServersRead)
            .WithName("ListPluginServers")
            .WithSummary("List the servers this token can reach");

        plugin.MapGet("/servers/{server}/status", async (
                string server,
                HttpContext http,
                [FromServices] IServerService servers,
                CancellationToken cancellationToken) =>
            {
                var principal = http.RequirePluginPrincipal();
                return Results.Ok(await DescribeServerAsync(servers, server, principal, cancellationToken));
            })
            .RequirePluginScopeForServer(PluginScopes.ServersRead)
            .WithName("GetPluginServerStatus")
            .WithSummary("Status of one server in reach");

        plugin.MapPost("/servers/{server}/start", async (
                string server,
                [FromServices] IServerService servers,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    await servers.StartServerAsync(server, cancellationToken);
                    return Results.Ok(new { server, action = "start", accepted = true });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(new { server, error = ex.Message });
                }
            })
            .RequirePluginScopeForServer(PluginScopes.ServersControl)
            .WithName("StartPluginServer")
            .WithSummary("Start a server in reach");

        plugin.MapPost("/servers/{server}/stop", async (
                string server,
                [FromQuery] int? timeoutSeconds,
                [FromServices] IServerService servers,
                [FromServices] ISettingsService settings,
                CancellationToken cancellationToken) =>
            {
                var timeout = await ServerEndpoints.ResolveShutdownTimeoutAsync(
                    settings, timeoutSeconds, cancellationToken);

                try
                {
                    await servers.StopServerAsync(server, timeout, cancellationToken);
                    return Results.Ok(new { server, action = "stop", accepted = true });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(new { server, error = ex.Message });
                }
            })
            .RequirePluginScopeForServer(PluginScopes.ServersControl)
            .WithName("StopPluginServer")
            .WithSummary("Stop a server in reach");

        plugin.MapPost("/servers/{server}/restart", async (
                string server,
                [FromQuery] int? timeoutSeconds,
                [FromServices] IServerService servers,
                [FromServices] ISettingsService settings,
                CancellationToken cancellationToken) =>
            {
                var timeout = await ServerEndpoints.ResolveShutdownTimeoutAsync(
                    settings, timeoutSeconds, cancellationToken);

                try
                {
                    await servers.RestartServerAsync(server, timeout, cancellationToken);
                    return Results.Ok(new { server, action = "restart", accepted = true });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(new { server, error = ex.Message });
                }
            })
            .RequirePluginScopeForServer(PluginScopes.ServersControl)
            .WithName("RestartPluginServer")
            .WithSummary("Restart a server in reach");

        plugin.MapGet("/servers/{server}/console", async (
                string server,
                [FromQuery] string? source,
                [FromQuery] int? lines,
                [FromServices] IConsoleService console,
                CancellationToken cancellationToken) =>
            {
                var logSource = string.Equals(source, "java", StringComparison.OrdinalIgnoreCase)
                    ? ConsoleLogSource.Java
                    : ConsoleLogSource.Server;

                return Results.Ok(await console.ReadRecentLogsAsync(
                    server, logSource, lines ?? 100, cancellationToken));
            })
            .RequirePluginScopeForServer(PluginScopes.ConsoleRead)
            .WithName("ReadPluginServerConsole")
            .WithSummary("Recent console output from a server in reach");

        plugin.MapPost("/servers/{server}/command", async (
                string server,
                [FromBody] PluginCommandRequest request,
                [FromServices] IConsoleService console,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request?.Command))
                {
                    return Results.BadRequest(new { error = "Command is required." });
                }

                try
                {
                    await console.SendCommandAsync(server, request.Command, cancellationToken);
                    return Results.Ok(new { server, accepted = true });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(new { server, error = ex.Message });
                }
            })
            .RequirePluginScopeForServer(PluginScopes.ConsoleWrite)
            .WithName("SendPluginServerCommand")
            .WithSummary("Send a console command to a server in reach");

        plugin.MapGet("/servers/{server}/players", async (
                string server,
                [FromServices] IPlayerService players,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    return Results.Ok(await players.ListPlayersAsync(server, cancellationToken));
                }
                catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
                {
                    // A server with no player files yet has no players, which is an
                    // empty list rather than an error a hub plugin has to special-case.
                    return Results.Ok(Array.Empty<PlayerSummaryDto>());
                }
            })
            .RequirePluginScopeForServer(PluginScopes.PlayersRead)
            .WithName("ListPluginServerPlayers")
            .WithSummary("Known players for a server in reach");

        plugin.MapPost("/servers/{server}/players/whitelist", async (
                string server,
                [FromBody] PluginPlayerRequest request,
                [FromServices] IPlayerService players,
                CancellationToken cancellationToken) =>
                await PlayerActionAsync(() => players.WhitelistPlayerAsync(
                    server, request.Uuid, request.Name, cancellationToken)))
            .RequirePluginScopeForServer(PluginScopes.PlayersWrite)
            .WithName("PluginWhitelistPlayer")
            .WithSummary("Whitelist a player on a server in reach");

        plugin.MapDelete("/servers/{server}/players/whitelist/{uuid}", async (
                string server,
                string uuid,
                [FromServices] IPlayerService players,
                CancellationToken cancellationToken) =>
                await PlayerActionAsync(() => players.RemoveWhitelistAsync(
                    server, uuid, cancellationToken)))
            .RequirePluginScopeForServer(PluginScopes.PlayersWrite)
            .WithName("PluginRemoveWhitelist")
            .WithSummary("Remove a player from the whitelist");

        plugin.MapPost("/servers/{server}/players/op", async (
                string server,
                [FromBody] PluginOpRequest request,
                [FromServices] IPlayerService players,
                CancellationToken cancellationToken) =>
                await PlayerActionAsync(() => players.OpPlayerAsync(
                    server,
                    request.Uuid,
                    request.Name,
                    request.Level ?? 4,
                    request.BypassesPlayerLimit ?? false,
                    cancellationToken)))
            .RequirePluginScopeForServer(PluginScopes.PlayersWrite)
            .WithName("PluginOpPlayer")
            .WithSummary("Op a player on a server in reach");

        plugin.MapDelete("/servers/{server}/players/op/{uuid}", async (
                string server,
                string uuid,
                [FromServices] IPlayerService players,
                CancellationToken cancellationToken) =>
                await PlayerActionAsync(() => players.DeopPlayerAsync(
                    server, uuid, cancellationToken)))
            .RequirePluginScopeForServer(PluginScopes.PlayersWrite)
            .WithName("PluginDeopPlayer")
            .WithSummary("Deop a player on a server in reach");

        plugin.MapPost("/servers/{server}/players/ban", async (
                string server,
                [FromBody] PluginBanRequest request,
                HttpContext http,
                [FromServices] IPlayerService players,
                CancellationToken cancellationToken) =>
            {
                var principal = http.RequirePluginPrincipal();
                return await PlayerActionAsync(() => players.BanPlayerAsync(
                    server,
                    request.Uuid,
                    request.Name,
                    request.Reason,
                    // Attributed to the token, not a person: a ban placed by a
                    // plugin should be traceable to which plugin placed it.
                    $"plugin:{principal.Name}",
                    request.ExpiresAt,
                    cancellationToken));
            })
            .RequirePluginScopeForServer(PluginScopes.PlayersWrite)
            .WithName("PluginBanPlayer")
            .WithSummary("Ban a player on a server in reach");

        plugin.MapDelete("/servers/{server}/players/ban/{uuid}", async (
                string server,
                string uuid,
                [FromServices] IPlayerService players,
                CancellationToken cancellationToken) =>
                await PlayerActionAsync(() => players.UnbanPlayerAsync(
                    server, uuid, cancellationToken)))
            .RequirePluginScopeForServer(PluginScopes.PlayersWrite)
            .WithName("PluginUnbanPlayer")
            .WithSummary("Unban a player on a server in reach");

        plugin.MapGet("/events", async (
                [FromQuery] int? limit,
                HttpContext http,
                [FromServices] IPluginTokenService tokens,
                CancellationToken cancellationToken) =>
            {
                var principal = http.RequirePluginPrincipal();
                return Results.Ok(await tokens.ListEventsAsync(
                    principal, limit ?? 100, cancellationToken));
            })
            .RequirePluginScope(PluginScopes.EventsRead)
            .WithName("ListPluginEvents")
            .WithSummary("Events already reported for this token's server");

        plugin.MapPost("/events", async (
                [FromBody] PluginEventRequest request,
                HttpContext http,
                [FromServices] IPluginTokenService tokens,
                CancellationToken cancellationToken) =>
            {
                var principal = http.RequirePluginPrincipal();

                try
                {
                    await tokens.RecordEventAsync(principal, request, cancellationToken);
                    return Results.Accepted();
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            })
            .RequirePluginScope(PluginScopes.EventsWrite)
            .WithName("RecordPluginEvent")
            .WithSummary("Report an event to the panel");

        return app;
    }

    /// <summary>
    /// Runs a player mutation, turning the states a server can legitimately be in
    /// into 409 rather than a 500. Shared by the whitelist/op/ban routes, which
    /// differ only in which call they make.
    /// </summary>
    private static async Task<IResult> PlayerActionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return Results.Ok(new { accepted = true });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Status is best-effort: a server that is stopped, mid-restart or missing its
    /// files still belongs in the list, reported as Unknown. Throwing here would
    /// mean one broken server hides every healthy one from a hub plugin.
    /// </summary>
    private static async Task<PluginServerDto> DescribeServerAsync(
        IServerService servers,
        string name,
        PluginTokenPrincipal principal,
        CancellationToken cancellationToken)
    {
        var isSelf = string.Equals(name, principal.ServerName, StringComparison.OrdinalIgnoreCase);

        try
        {
            var heartbeat = await servers.GetServerStatusAsync(name, cancellationToken);
            return new PluginServerDto(
                heartbeat.Name,
                heartbeat.Status,
                isSelf,
                heartbeat.Ping?.PlayersOnline,
                heartbeat.Ping?.PlayersMax);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
        {
            return new PluginServerDto(name, nameof(ServerStatus.Unknown), isSelf, null, null);
        }
    }
}
