using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;
using MineOS.Domain.Entities;

namespace MineOS.Api.Endpoints;

/// <summary>
/// Operator-facing management of plugin tokens. Admin only, and on the panel's
/// normal authenticated path — an operator mints these from the panel, then pastes
/// the secret into a plugin's config.
/// </summary>
public static class PluginTokenAdminEndpoints
{
    public static IEndpointRouteBuilder MapPluginTokenAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var tokens = app.MapGroup("/plugin-tokens")
            .WithTags("Plugin tokens")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "admin" });

        tokens.MapGet("", async (
                [FromQuery] string? serverName,
                [FromServices] IPluginTokenService service,
                CancellationToken cancellationToken) =>
                Results.Ok(await service.ListAsync(serverName, cancellationToken)))
            .WithName("ListPluginTokens")
            .WithSummary("List issued plugin tokens");

        // Descriptions ship with the list so the panel explains what it is about
        // to grant without keeping its own copy of the wording.
        tokens.MapGet("/scopes", () => Results.Ok(
                PluginScopes.All
                    .Select(scope => new PluginScopeDto(scope, PluginScopes.Describe(scope)))
                    .ToList()))
            .WithName("ListPluginScopes")
            .WithSummary("List the scopes a plugin token can be granted, and what each allows");

        tokens.MapPost("", async (
                [FromBody] IssuePluginTokenRequest request,
                ClaimsPrincipal user,
                [FromServices] IPluginTokenService service,
                CancellationToken cancellationToken) =>
            {
                TryGetUserId(user, out var userId);

                try
                {
                    var issued = await service.IssueAsync(request, userId, cancellationToken);

                    // The secret appears in this response and nowhere else, ever.
                    return Results.Created($"/api/v1/plugin-tokens/{issued.Token.Id}", issued);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            })
            .WithName("IssuePluginToken")
            .WithSummary("Issue a plugin token (returns the secret once)");

        tokens.MapDelete("/{id:int}", async (
                int id,
                [FromServices] IPluginTokenService service,
                CancellationToken cancellationToken) =>
                await service.RevokeAsync(id, cancellationToken)
                    ? Results.Ok(new { id, revoked = true })
                    : Results.NotFound(new { error = $"No plugin token {id}." }))
            .WithName("RevokePluginToken")
            .WithSummary("Revoke a plugin token");

        return app;
    }

    private static bool TryGetUserId(ClaimsPrincipal user, out int userId)
    {
        userId = 0;
        var claim = user.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
            ?? user.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out userId);
    }
}
