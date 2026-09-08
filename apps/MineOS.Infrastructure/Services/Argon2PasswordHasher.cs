using Isopoh.Cryptography.Argon2;
using Microsoft.Extensions.Options;
using MineOS.Application.Interfaces;
using MineOS.Application.Options;

namespace MineOS.Infrastructure.Services;

public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private readonly PasswordHashingOptions _options;

    public Argon2PasswordHasher(IOptions<PasswordHashingOptions>? options = null)
    {
        _options = options?.Value ?? new PasswordHashingOptions();
    }

    public string Hash(string password)
    {
        // Explicit work factors rather than the library defaults (64 MiB, 3
        // passes), which cost ~150 ms per sign-in on a desktop and far more on a
        // small host. See PasswordHashingOptions for why these values.
        return Argon2.Hash(
            password: password,
            timeCost: _options.Iterations,
            memoryCost: _options.MemoryKb,
            parallelism: _options.Parallelism);
    }

    public bool Verify(string password, string hash)
    {
        // Verification reads the work factors out of the hash itself, so hashes
        // made under the old settings keep working unchanged.
        return Argon2.Verify(hash, password);
    }

    public bool NeedsRehash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return true;
        }

        var parameters = ParseParameters(hash);
        if (parameters is null)
        {
            // Unreadable, or not an Argon2 hash at all. Re-hash it on the next
            // successful sign-in rather than leaving something unrecognised in
            // the users table.
            return true;
        }

        var (memoryKb, iterations, parallelism) = parameters.Value;
        return memoryKb != _options.MemoryKb
            || iterations != _options.Iterations
            || parallelism != _options.Parallelism;
    }

    /// <summary>
    /// Pulls m/t/p out of an encoded hash, e.g.
    /// <c>$argon2id$v=19$m=19456,t=2,p=1$salt$digest</c>.
    /// Returns null when the string is not in that shape.
    /// </summary>
    private static (int MemoryKb, int Iterations, int Parallelism)? ParseParameters(string hash)
    {
        var segments = hash.Split('$', StringSplitOptions.RemoveEmptyEntries);
        var parameterSegment = segments.FirstOrDefault(s => s.StartsWith("m=", StringComparison.Ordinal));
        if (parameterSegment is null)
        {
            return null;
        }

        int? memoryKb = null;
        int? iterations = null;
        int? parallelism = null;

        foreach (var pair in parameterSegment.Split(','))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out var value))
            {
                continue;
            }

            switch (parts[0])
            {
                case "m": memoryKb = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
            }
        }

        return memoryKb.HasValue && iterations.HasValue && parallelism.HasValue
            ? (memoryKb.Value, iterations.Value, parallelism.Value)
            : null;
    }
}
