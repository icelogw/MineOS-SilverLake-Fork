using MineOS.Api.Middleware;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;

namespace MineOS.Api.Authorization;

/// <summary>
/// Authenticates an inbound plugin request and checks it carries a required scope.
///
/// Plugin requests deliberately do not travel the <see cref="ApiKeyMiddleware"/>
/// path: a valid X-Api-Key is promoted to an admin principal, which is exactly the
/// authority a game server must never hold. Plugin routes are marked
/// <see cref="SkipApiKeyAttribute"/> and authenticate here instead, against a token
/// that is bound to one server.
/// </summary>
public sealed class PluginScopeFilter : IEndpointFilter
{
    public const string HeaderName = "X-Plugin-Token";

    /// <summary>Where the authenticated identity is parked for the rest of the request.</summary>
    public const string PrincipalItemKey = "Plugin.TokenPrincipal";

    /// <summary>Null means "any valid token will do" — used by /me, which tells a
    /// plugin what it is allowed to do and so cannot itself require a scope.</summary>
    private readonly string? _requiredScope;

    public PluginScopeFilter(string? requiredScope)
    {
        _requiredScope = requiredScope;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (!http.Request.Headers.TryGetValue(HeaderName, out var header) ||
            string.IsNullOrWhiteSpace(header))
        {
            return Results.Problem(
                $"Missing {HeaderName} header.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var tokens = http.RequestServices.GetRequiredService<IPluginTokenService>();
        var principal = await tokens.AuthenticateAsync(header.ToString(), http.RequestAborted);

        if (principal == null)
        {
            // Unknown, revoked and expired are one answer on purpose. Telling a
            // caller which of the three it hit is free reconnaissance.
            return Results.Problem(
                "Invalid plugin token.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (_requiredScope != null && !principal.HasScope(_requiredScope))
        {
            return Results.Problem(
                $"Token is missing the required scope '{_requiredScope}'.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        http.Items[PrincipalItemKey] = principal;

        return await next(context);
    }
}

/// <summary>
/// For routes that name a target server: confirms the calling token may act on it.
/// Runs after <see cref="PluginScopeFilter"/>, which has already established who
/// is calling.
/// </summary>
public sealed class PluginServerAccessFilter : IEndpointFilter
{
    public const string RouteValueName = "server";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        var principal = http.GetPluginPrincipal();
        if (principal == null)
        {
            return Results.Problem(
                "Plugin authentication did not run.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        if (!http.Request.RouteValues.TryGetValue(RouteValueName, out var raw) ||
            raw?.ToString() is not { Length: > 0 } targetServer)
        {
            return Results.Problem("Server name is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var tokens = http.RequestServices.GetRequiredService<IPluginTokenService>();
        if (!await tokens.CanActOnServerAsync(principal, targetServer, http.RequestAborted))
        {
            // 404, not 403. A proxy plugin probing names would otherwise be able
            // to map which servers exist on the panel by reading the difference.
            return Results.Problem(
                $"No server '{targetServer}' in reach of this token.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return await next(context);
    }
}

public static class PluginAuthExtensions
{
    /// <summary>
    /// Marks a route as plugin-authenticated: skips the admin API-key middleware
    /// and requires a plugin token carrying <paramref name="scope"/>.
    /// </summary>
    public static RouteHandlerBuilder RequirePluginScope(this RouteHandlerBuilder builder, string scope) =>
        builder
            .WithMetadata(new SkipApiKeyAttribute())
            .AddEndpointFilter(new PluginScopeFilter(scope));

    /// <summary>Requires a valid plugin token but no particular scope.</summary>
    public static RouteHandlerBuilder RequirePluginToken(this RouteHandlerBuilder builder) =>
        builder
            .WithMetadata(new SkipApiKeyAttribute())
            .AddEndpointFilter(new PluginScopeFilter(null));

    /// <summary>
    /// As <see cref="RequirePluginScope"/>, and additionally confirms the token may
    /// act on the server named by the route's {server} segment.
    /// </summary>
    public static RouteHandlerBuilder RequirePluginScopeForServer(this RouteHandlerBuilder builder, string scope) =>
        builder
            .RequirePluginScope(scope)
            .AddEndpointFilter(new PluginServerAccessFilter());

    public static PluginTokenPrincipal? GetPluginPrincipal(this HttpContext context) =>
        context.Items.TryGetValue(PluginScopeFilter.PrincipalItemKey, out var value)
            ? value as PluginTokenPrincipal
            : null;

    /// <summary>
    /// The principal, or a throw. Only valid inside a handler behind
    /// <see cref="RequirePluginScope"/>, where the filter guarantees it is set.
    /// </summary>
    public static PluginTokenPrincipal RequirePluginPrincipal(this HttpContext context) =>
        context.GetPluginPrincipal()
        ?? throw new InvalidOperationException(
            "No plugin principal on the request. Did the route forget RequirePluginScope?");
}
