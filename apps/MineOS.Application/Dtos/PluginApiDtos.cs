namespace MineOS.Application.Dtos;

/// <summary>
/// The authenticated identity behind an inbound plugin request: which server is
/// calling, and what it is allowed to do. Produced by authenticating a token and
/// carried for the life of the request.
/// </summary>
public sealed record PluginTokenPrincipal(
    int TokenId,
    string Name,
    string ServerName,
    IReadOnlySet<string> Scopes,
    bool AllowProxyBackends)
{
    public bool HasScope(string scope) => Scopes.Contains(scope);
}

/// <summary>A plugin token as shown to an operator. Never carries the secret.</summary>
public sealed record PluginTokenDto(
    int Id,
    string Name,
    string ServerName,
    string TokenPrefix,
    IReadOnlyList<string> Scopes,
    bool AllowProxyBackends,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    bool Revoked);

public sealed record IssuePluginTokenRequest(
    string Name,
    string ServerName,
    IReadOnlyList<string> Scopes,
    bool AllowProxyBackends = false,
    DateTimeOffset? ExpiresAt = null);

/// <summary>
/// The one and only time the secret is available. Returned from issuance and
/// never reconstructible afterwards.
/// </summary>
public sealed record IssuedPluginTokenDto(PluginTokenDto Token, string Secret);

/// <summary>What a plugin is told about itself when it calls /me.</summary>
public sealed record PluginIdentityDto(
    string TokenName,
    string ServerName,
    IReadOnlyList<string> Scopes,
    bool AllowProxyBackends,
    IReadOnlyList<string> ServersInReach);

/// <summary>Compact server view for plugins — deliberately less than the panel's
/// own server DTO, which exposes paths and host details a plugin has no use for.</summary>
public sealed record PluginServerDto(
    string Name,
    string Status,
    bool IsSelf,
    int? PlayersOnline,
    int? PlayersMax);

public sealed record PluginCommandRequest(string Command);

public sealed record PluginEventRequest(
    string Type,
    string? PlayerUuid = null,
    string? PlayerName = null,
    string? Data = null,
    DateTimeOffset? OccurredAt = null);

/// <summary>A grantable scope and what it allows, for the token-issuing UI.</summary>
public sealed record PluginScopeDto(string Scope, string Description);

/// <summary>An event previously reported by a plugin, as read back.</summary>
public sealed record PluginEventDto(
    int Id,
    string Type,
    string? PlayerUuid,
    string? PlayerName,
    string? Data,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt);

/// <summary>Identifies a player for a whitelist or unban action.</summary>
public sealed record PluginPlayerRequest(string Uuid, string? Name = null);

public sealed record PluginOpRequest(
    string Uuid,
    string? Name = null,
    int? Level = null,
    bool? BypassesPlayerLimit = null);

public sealed record PluginBanRequest(
    string Uuid,
    string? Name = null,
    string? Reason = null,
    DateTimeOffset? ExpiresAt = null);
