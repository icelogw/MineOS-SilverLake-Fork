namespace MineOS.Domain.Entities;

/// <summary>
/// Something a plugin told the panel happened on its server.
///
/// Intentionally schema-light: the panel cannot know what a third-party plugin
/// considers an event, so the shape is a type string plus a JSON payload the
/// panel stores but does not interpret.
/// </summary>
public sealed class PluginEvent
{
    public int Id { get; set; }

    /// <summary>The server the event is about — resolved from the token, not
    /// taken from the request body, so a plugin cannot file events against a
    /// server it has no authority over.</summary>
    public required string ServerName { get; set; }

    /// <summary>Plugin-defined event type, e.g. "player.join" or "arena.start".</summary>
    public required string Type { get; set; }

    public string? PlayerUuid { get; set; }
    public string? PlayerName { get; set; }

    /// <summary>Opaque JSON payload. Stored verbatim, never executed or expanded.</summary>
    public string? Data { get; set; }

    /// <summary>When the plugin says it happened; defaults to receipt time.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>When the panel accepted it. Kept separately because a plugin's
    /// clock is not the panel's clock.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Which token filed it, for attribution and for cleanup when a
    /// token is revoked.</summary>
    public int TokenId { get; set; }
}
