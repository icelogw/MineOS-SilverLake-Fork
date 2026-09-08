using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;
using MineOS.Domain.Entities;
using MineOS.Infrastructure.Persistence;

namespace MineOS.Infrastructure.Services;

public sealed class PluginTokenService : IPluginTokenService
{
    /// <summary>
    /// Marks a string as a MineOS plugin token. Worth the bytes: operators paste
    /// these into plugin configs, and a distinctive prefix makes a leaked one
    /// recognisable in a paste, a log or a public repository.
    /// </summary>
    private const string TokenPrefixMarker = "mosp_";

    /// <summary>
    /// How much of the token is kept in clear for display. Twelve characters
    /// covers the marker plus a few random ones - enough to tell tokens apart,
    /// far too few to help guess the remaining 256 bits.
    /// </summary>
    private const int DisplayPrefixLength = 12;

    /// <summary>
    /// A busy plugin may call several times a second. Last-used exists so an
    /// operator can tell whether a token is still in use, so minute resolution is
    /// ample - and it keeps read-only endpoints from writing on every request.
    /// </summary>
    private static readonly TimeSpan LastUsedWriteThrottle = TimeSpan.FromMinutes(1);

    private readonly AppDbContext _db;
    private readonly IProxyForwardingService _proxyForwarding;
    private readonly TimeProvider _timeProvider;

    public PluginTokenService(
        AppDbContext db,
        IProxyForwardingService proxyForwarding,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _proxyForwarding = proxyForwarding;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PluginTokenPrincipal?> AuthenticateAsync(string secret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        var hash = HashToken(secret);
        var token = await _db.PluginTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token == null || token.Revoked)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        if (token.ExpiresAt.HasValue && token.ExpiresAt.Value <= now)
        {
            return null;
        }

        if (!token.LastUsedAt.HasValue || now - token.LastUsedAt.Value >= LastUsedWriteThrottle)
        {
            token.LastUsedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new PluginTokenPrincipal(
            token.Id,
            token.Name,
            token.ServerName,
            ParseScopes(token.Scopes),
            token.AllowProxyBackends);
    }

    public async Task<bool> CanActOnServerAsync(
        PluginTokenPrincipal principal,
        string targetServer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetServer))
        {
            return false;
        }

        if (string.Equals(principal.ServerName, targetServer, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!principal.AllowProxyBackends)
        {
            return false;
        }

        var backends = await GetProxyBackendsAsync(principal.ServerName, cancellationToken);
        return backends.Contains(targetServer, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<string>> ListServersInReachAsync(
        PluginTokenPrincipal principal,
        CancellationToken cancellationToken)
    {
        var reach = new List<string> { principal.ServerName };

        if (principal.AllowProxyBackends)
        {
            var backends = await GetProxyBackendsAsync(principal.ServerName, cancellationToken);
            foreach (var backend in backends)
            {
                if (!reach.Contains(backend, StringComparer.OrdinalIgnoreCase))
                {
                    reach.Add(backend);
                }
            }
        }

        return reach;
    }

    /// <summary>
    /// The proxy's backend list, derived from the proxy's own configuration.
    /// A server that fronts nothing is not an error - a token may carry proxy
    /// authority before its server has been set up as a proxy - so an
    /// unreadable or absent proxy config yields an empty reach, never a 500.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetProxyBackendsAsync(
        string proxyName,
        CancellationToken cancellationToken)
    {
        try
        {
            var summary = await _proxyForwarding.GetProxyBackendsAsync(proxyName, cancellationToken);
            return summary.Backends.Select(b => b.ServerName).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
        {
            return Array.Empty<string>();
        }
    }

    public async Task<IssuedPluginTokenDto> IssueAsync(
        IssuePluginTokenRequest request,
        int createdByUserId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Token name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ServerName))
        {
            throw new ArgumentException("Server name is required.", nameof(request));
        }

        var scopes = (request.Scopes ?? Array.Empty<string>())
            .Select(s => s?.Trim() ?? string.Empty)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (scopes.Count == 0)
        {
            throw new ArgumentException("At least one scope is required.", nameof(request));
        }

        var unknown = scopes.Where(s => !PluginScopes.IsKnown(s)).ToList();
        if (unknown.Count > 0)
        {
            // Refuse rather than silently drop. A mistyped scope that is quietly
            // discarded mints a token that looks correct and fails in the field,
            // where the plugin author has no way to see why.
            throw new ArgumentException(
                $"Unknown scope(s): {string.Join(", ", unknown)}. Valid scopes: {string.Join(", ", PluginScopes.All)}.",
                nameof(request));
        }

        var secret = GenerateSecret();
        var now = _timeProvider.GetUtcNow();

        var token = new PluginToken
        {
            TokenHash = HashToken(secret),
            TokenPrefix = secret[..DisplayPrefixLength],
            Name = request.Name.Trim(),
            ServerName = request.ServerName.Trim(),
            Scopes = string.Join(',', scopes),
            AllowProxyBackends = request.AllowProxyBackends,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            ExpiresAt = request.ExpiresAt
        };

        _db.PluginTokens.Add(token);
        await _db.SaveChangesAsync(cancellationToken);

        return new IssuedPluginTokenDto(ToDto(token), secret);
    }

    public async Task<IReadOnlyList<PluginTokenDto>> ListAsync(string? serverName, CancellationToken cancellationToken)
    {
        var query = _db.PluginTokens.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            query = query.Where(t => t.ServerName == serverName);
        }

        // Ordered client-side: SQLite cannot ORDER BY a DateTimeOffset, and the
        // list of tokens for an installation is small enough that sorting after
        // materializing costs nothing. Same reason PerformanceMetric timestamps
        // were converted to Unix time rather than ordered in SQL.
        var tokens = await query.ToListAsync(cancellationToken);

        return tokens
            .OrderByDescending(t => t.CreatedAt)
            .Select(ToDto)
            .ToList();
    }

    public async Task<bool> RevokeAsync(int id, CancellationToken cancellationToken)
    {
        var token = await _db.PluginTokens.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (token == null)
        {
            return false;
        }

        if (token.Revoked)
        {
            return true;
        }

        token.Revoked = true;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task RecordEventAsync(
        PluginTokenPrincipal principal,
        PluginEventRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Type))
        {
            throw new ArgumentException("Event type is required.", nameof(request));
        }

        var now = _timeProvider.GetUtcNow();

        _db.PluginEvents.Add(new PluginEvent
        {
            // Taken from the token, never the body: a plugin files events about
            // its own server only, so there is nothing here for a caller to forge.
            ServerName = principal.ServerName,
            Type = request.Type.Trim(),
            PlayerUuid = request.PlayerUuid,
            PlayerName = request.PlayerName,
            Data = request.Data,
            OccurredAt = request.OccurredAt ?? now,
            ReceivedAt = now,
            TokenId = principal.TokenId
        });

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PluginEventDto>> ListEventsAsync(
        PluginTokenPrincipal principal,
        int limit,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit, 1, 500);

        // Ordered and paged client-side: SQLite cannot ORDER BY a DateTimeOffset,
        // the same limitation that applies to the token list. Filtering happens in
        // SQL, so only this server's rows are materialized.
        var events = await _db.PluginEvents
            .AsNoTracking()
            .Where(e => e.ServerName == principal.ServerName)
            .ToListAsync(cancellationToken);

        return events
            .OrderByDescending(e => e.OccurredAt)
            .Take(take)
            .Select(e => new PluginEventDto(
                e.Id,
                e.Type,
                e.PlayerUuid,
                e.PlayerName,
                e.Data,
                e.OccurredAt,
                e.ReceivedAt))
            .ToList();
    }

    private static PluginTokenDto ToDto(PluginToken token) => new(
        token.Id,
        token.Name,
        token.ServerName,
        token.TokenPrefix,
        ParseScopes(token.Scopes).ToList(),
        token.AllowProxyBackends,
        token.CreatedAt,
        token.ExpiresAt,
        token.LastUsedAt,
        token.Revoked);

    private static HashSet<string> ParseScopes(string scopes) =>
        scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    private static string GenerateSecret()
    {
        // 32 bytes of CSPRNG output, URL-safe so it survives config files,
        // environment variables and headers without escaping.
        var bytes = RandomNumberGenerator.GetBytes(32);
        var body = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return TokenPrefixMarker + body;
    }

    internal static string HashToken(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
