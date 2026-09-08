using MineOS.Application.Dtos;

namespace MineOS.Application.Interfaces;

/// <summary>
/// Issues, authenticates and authorizes the credentials Minecraft plugins use to
/// call the panel.
/// </summary>
public interface IPluginTokenService
{
    /// <summary>
    /// Resolves a presented secret to an identity, or null when the token is
    /// unknown, revoked or expired. Callers must not distinguish those cases to
    /// the caller — an attacker learns nothing from a uniform failure.
    /// </summary>
    Task<PluginTokenPrincipal?> AuthenticateAsync(string secret, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this identity may act on <paramref name="targetServer"/>: always
    /// true for its own server, and true for a backend of its proxy when the
    /// token was issued with proxy authority.
    /// </summary>
    Task<bool> CanActOnServerAsync(
        PluginTokenPrincipal principal,
        string targetServer,
        CancellationToken cancellationToken);

    /// <summary>Every server this identity can currently reach, own server first.</summary>
    Task<IReadOnlyList<string>> ListServersInReachAsync(
        PluginTokenPrincipal principal,
        CancellationToken cancellationToken);

    /// <summary>
    /// Mints a token. The secret in the result is the only copy that will ever
    /// exist; only its hash is stored.
    /// </summary>
    Task<IssuedPluginTokenDto> IssueAsync(
        IssuePluginTokenRequest request,
        int createdByUserId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PluginTokenDto>> ListAsync(string? serverName, CancellationToken cancellationToken);

    /// <summary>Revokes a token. Idempotent; returns false when no such token.</summary>
    Task<bool> RevokeAsync(int id, CancellationToken cancellationToken);

    Task RecordEventAsync(
        PluginTokenPrincipal principal,
        PluginEventRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Events already reported for this identity's own server, most recent first.
    /// Scoped to the token's server for the same reason writes are: a plugin reads
    /// its own server's events, never another's.
    /// </summary>
    Task<IReadOnlyList<PluginEventDto>> ListEventsAsync(
        PluginTokenPrincipal principal,
        int limit,
        CancellationToken cancellationToken);
}
