namespace MineOS.Application.Interfaces;

public interface IImportService
{
    /// <summary>
    /// Unpacks an uploaded archive into a new server.
    /// </summary>
    /// <param name="serverName">
    /// The label the operator asked for. It may contain spaces and is stored as the
    /// display name; the directory on disk is a slug of it.
    /// </param>
    /// <returns>
    /// The server's backend name — the slug, not the label. Callers must use this for
    /// any subsequent lookup: passing the requested label back into
    /// <see cref="IServerService"/> looks for a directory that was never created.
    /// </returns>
    Task<string> CreateServerFromImportAsync(string filename, string serverName, CancellationToken cancellationToken);
    Task<string> SaveImportAsync(string filename, Stream content, CancellationToken cancellationToken);
    Task DeleteImportAsync(string filename, CancellationToken cancellationToken);
}
