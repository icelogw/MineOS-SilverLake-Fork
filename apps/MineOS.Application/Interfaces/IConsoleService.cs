using MineOS.Application.Dtos;

namespace MineOS.Application.Interfaces;

public interface IConsoleService
{
    Task SendCommandAsync(string serverName, string command, CancellationToken cancellationToken);
    Task ClearLogsAsync(string serverName, ConsoleLogSource source, CancellationToken cancellationToken);
    IAsyncEnumerable<LogEntryDto> StreamLogsAsync(
        string serverName,
        ConsoleLogSource source,
        CancellationToken cancellationToken);

    /// <summary>
    /// The most recent log lines, oldest first, without holding a connection open.
    /// The streaming variant suits a live console; a plugin polling for the last
    /// few lines should not have to keep a socket for it.
    /// </summary>
    Task<IReadOnlyList<LogEntryDto>> ReadRecentLogsAsync(
        string serverName,
        ConsoleLogSource source,
        int maxLines,
        CancellationToken cancellationToken);
}
