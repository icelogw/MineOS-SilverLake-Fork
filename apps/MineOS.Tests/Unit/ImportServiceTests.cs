using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MineOS.Application.Options;
using MineOS.Infrastructure.Services;

namespace MineOS.Tests.Unit;

/// <summary>
/// Pins the contract that broke server import: the name the import returns is the
/// one every later lookup must use.
/// </summary>
public class ImportServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ImportService _service;
    private readonly HostOptions _options;

    public ImportServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mineos-import-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _options = new HostOptions
        {
            BaseDirectory = _root,
            ServersPathSegment = "servers",
            ImportPathSegment = "import",
            BackupsPathSegment = "backups",
            ProfilesPathSegment = "profiles"
        };

        _service = new ImportService(
            Options.Create(_options),
            Mock.Of<ILogger<ImportService>>());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private string ServersPath => Path.Combine(_root, _options.ServersPathSegment);

    /// <summary>Writes a minimal server archive into the import directory.</summary>
    private async Task<string> PlantArchiveAsync(string filename = "template.zip")
    {
        var importPath = Path.Combine(_root, _options.ImportPathSegment);
        Directory.CreateDirectory(importPath);

        await using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("server.properties");
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("level-name=world\n"));
        }

        buffer.Position = 0;
        await _service.SaveImportAsync(filename, buffer, CancellationToken.None);
        return filename;
    }

    [Fact]
    public async Task Import_returns_the_backend_name_not_the_requested_label()
    {
        var archive = await PlantArchiveAsync();

        var returned = await _service.CreateServerFromImportAsync(archive, "test 1", CancellationToken.None);

        // The regression: the endpoint used to look the server up by "test 1",
        // which is a label, not a directory. The job then failed at 90% on an
        // import that had actually succeeded.
        Assert.NotEqual("test 1", returned);
        Assert.StartsWith("test-1-", returned);

        // Whatever comes back must be directly resolvable on disk.
        Assert.True(
            Directory.Exists(Path.Combine(ServersPath, returned)),
            $"Import returned '{returned}', which is not a directory under {ServersPath}.");
    }

    [Fact]
    public async Task Returned_name_is_a_bare_name_not_a_path()
    {
        var archive = await PlantArchiveAsync();

        var returned = await _service.CreateServerFromImportAsync(archive, "hub", CancellationToken.None);

        // IServerService takes a name. Returning a full path would fail the same
        // way the label did, just less obviously.
        Assert.Equal(returned, Path.GetFileName(returned));
        Assert.DoesNotContain(Path.DirectorySeparatorChar, returned);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, returned);
    }

    [Fact]
    public async Task A_label_that_is_already_slug_safe_still_gets_a_distinct_directory()
    {
        // Even "hub" becomes "hub-<suffix>", so a caller can never assume the
        // requested name and the created name are the same.
        var archive = await PlantArchiveAsync();

        var returned = await _service.CreateServerFromImportAsync(archive, "hub", CancellationToken.None);

        Assert.NotEqual("hub", returned);
        Assert.StartsWith("hub-", returned);
    }

    [Fact]
    public async Task The_requested_label_is_kept_as_the_display_name()
    {
        var archive = await PlantArchiveAsync();

        var returned = await _service.CreateServerFromImportAsync(archive, "test 1", CancellationToken.None);

        var config = await File.ReadAllTextAsync(
            Path.Combine(ServersPath, returned, "server.config"));

        Assert.Contains("test 1", config);
    }
}
