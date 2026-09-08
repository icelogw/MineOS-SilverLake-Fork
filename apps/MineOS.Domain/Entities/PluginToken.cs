namespace MineOS.Domain.Entities;

/// <summary>
/// A credential issued to a Minecraft plugin so it can call the panel back.
///
/// Deliberately NOT an <see cref="ApiKey"/>. A valid ApiKey is a full-access admin
/// identity, and these tokens live in plaintext config on game servers that run
/// third-party code and accept connections from the public. A leaked plugin token
/// must cost the operator one server, never the panel.
/// </summary>
public sealed class PluginToken
{
    public int Id { get; set; }

    /// <summary>
    /// SHA-256 of the token, lowercase hex. The token itself is shown once at
    /// creation and never persisted, so a copy of the database yields no usable
    /// credentials. Unsalted on purpose: the token is 256 bits of CSPRNG output,
    /// so there is no guessable input to protect, and lookup must be a single
    /// indexed read rather than a scan over every row.
    /// </summary>
    public required string TokenHash { get; set; }

    /// <summary>
    /// Leading characters of the token, kept in clear so the UI can tell two
    /// tokens apart and an operator can match one to the server it was pasted
    /// into. Too short to narrow a brute-force search.
    /// </summary>
    public required string TokenPrefix { get; set; }

    /// <summary>Operator-facing label, e.g. "hub velocity plugin".</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The server this token was issued to. Every authorization decision starts
    /// here — a token is never ambient panel access, it is always "this server,
    /// acting on itself".
    /// </summary>
    public required string ServerName { get; set; }

    /// <summary>
    /// Granted scopes, comma-separated. See <see cref="PluginScopes"/>.
    /// </summary>
    public required string Scopes { get; set; }

    /// <summary>
    /// Widens the token from "this server" to "this server and the backends it
    /// fronts", for a token issued to a Velocity/BungeeCord proxy. The backend
    /// list is derived from the proxy's own config at request time rather than
    /// stored, so removing a server from the proxy also removes the plugin's
    /// authority over it — no second place to forget to update.
    /// </summary>
    public bool AllowProxyBackends { get; set; }

    public int CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Null means no expiry. Enforced at authentication time.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Written lazily (see the throttle in the token service) so a chatty plugin
    /// does not turn every request into a database write.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public bool Revoked { get; set; }
}

/// <summary>
/// The capabilities a plugin token can carry. Kept as strings because they are
/// persisted and appear on the wire; kept in one place so the API, the issuing
/// UI and validation cannot drift apart.
/// </summary>
public static class PluginScopes
{
    /// <summary>List servers in reach and read their status.</summary>
    public const string ServersRead = "servers:read";

    /// <summary>Start, stop and restart servers in reach.</summary>
    public const string ServersControl = "servers:control";

    /// <summary>Read recent console output from a server in reach.</summary>
    public const string ConsoleRead = "console:read";

    /// <summary>Send a console command to a server in reach.</summary>
    public const string ConsoleWrite = "console:write";

    /// <summary>Read events previously reported for a server in reach.</summary>
    public const string EventsRead = "events:read";

    /// <summary>Report events (player joins, custom plugin events) to the panel.</summary>
    public const string EventsWrite = "events:write";

    /// <summary>Read the known-players list for servers in reach.</summary>
    public const string PlayersRead = "players:read";

    /// <summary>Change player state — whitelist, op and ban — on servers in reach.</summary>
    public const string PlayersWrite = "players:write";

    /// <summary>
    /// Every scope, in read/write pairs. Each capability has both halves so a
    /// plugin that only needs to look at something is never handed the ability to
    /// change it — the reason a plugin token exists at all.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        ServersRead,
        ServersControl,
        ConsoleRead,
        ConsoleWrite,
        EventsRead,
        EventsWrite,
        PlayersRead,
        PlayersWrite
    };

    /// <summary>
    /// What each scope lets a plugin do, in the words an operator needs to make
    /// the decision. Kept beside the scopes themselves so the panel cannot show a
    /// description that no longer matches what the scope actually grants.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServersRead] =
                "See which servers this token can reach, and read their status and player counts.",
            [ServersControl] =
                "Start, stop and restart those servers. A plugin with this can take a server offline.",
            [ConsoleRead] =
                "Read recent console output from those servers. Read-only; cannot run commands.",
            [ConsoleWrite] =
                "Run console commands on those servers, with the same authority as typing in the panel console — including op and ban.",
            [EventsRead] =
                "Read events already reported for those servers.",
            [EventsWrite] =
                "Report events to the panel (player joins, custom plugin events). Write-only; grants no read access.",
            [PlayersRead] =
                "Read the known-player list for those servers, including UUIDs, whitelist and ban state.",
            [PlayersWrite] =
                "Whitelist, op, ban and unban players on those servers."
        };

    public static bool IsKnown(string scope) =>
        All.Contains(scope, StringComparer.Ordinal);

    public static string Describe(string scope) =>
        Descriptions.TryGetValue(scope, out var description) ? description : "Unknown scope.";
}
