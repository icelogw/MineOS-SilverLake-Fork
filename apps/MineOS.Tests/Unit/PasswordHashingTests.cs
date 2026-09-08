using System.Diagnostics;
using Microsoft.Extensions.Options;
using MineOS.Application.Options;
using MineOS.Infrastructure.Services;

namespace MineOS.Tests.Unit;

/// <summary>
/// Password hashing is deliberately slow, but the cost lands on every sign-in.
/// These pin the parameters actually used and the compatibility guarantee.
/// </summary>
public class PasswordHashingTests
{
    private static Argon2PasswordHasher Hasher(PasswordHashingOptions? options = null) =>
        new(Options.Create(options ?? new PasswordHashingOptions()));

    [Fact]
    public void Defaults_are_the_owasp_argon2id_profile()
    {
        var options = new PasswordHashingOptions();

        // 19 MiB / 2 passes / 1 lane. The library's own defaults are 64 MiB and
        // 3 passes, which cost roughly three times as much per sign-in for no
        // recognised gain in strength.
        Assert.Equal(19456, options.MemoryKb);
        Assert.Equal(2, options.Iterations);
        Assert.Equal(1, options.Parallelism);
    }

    [Fact]
    public void The_configured_work_factors_are_written_into_the_hash()
    {
        var hash = Hasher().Hash("correct horse battery staple");

        Assert.Contains("$argon2id$", hash);
        Assert.Contains("m=19456", hash);
        Assert.Contains("t=2", hash);
        Assert.Contains("p=1", hash);
    }

    [Fact]
    public void A_hash_verifies_against_its_own_password_and_nothing_else()
    {
        var hasher = Hasher();
        var hash = hasher.Hash("admin123!");

        Assert.True(hasher.Verify("admin123!", hash));
        Assert.False(hasher.Verify("admin123", hash));
        Assert.False(hasher.Verify("", hash));
    }

    [Fact]
    public void Hashes_made_under_the_old_heavier_settings_still_verify()
    {
        // The upgrade must not lock anyone out. Argon2 stores its parameters in
        // the hash, so a password hashed before this change keeps working — it is
        // verified with the settings it was created under.
        var legacy = Hasher(new PasswordHashingOptions
        {
            MemoryKb = 65536,
            Iterations = 3,
            Parallelism = 1
        }).Hash("admin123!");

        Assert.Contains("m=65536", legacy);
        Assert.Contains("t=3", legacy);

        // Verified by a hasher configured with the new, cheaper settings.
        Assert.True(Hasher().Verify("admin123!", legacy));
        Assert.False(Hasher().Verify("wrong", legacy));
    }

    [Fact]
    public void Raising_the_work_factors_costs_measurably_more()
    {
        // Guards the direction of the trade: these settings are what makes
        // hashing expensive, so a change that made them a no-op would be a
        // silent security regression rather than a speed-up.
        var cheap = new PasswordHashingOptions { MemoryKb = 8192, Iterations = 1, Parallelism = 1 };
        var dear = new PasswordHashingOptions { MemoryKb = 65536, Iterations = 4, Parallelism = 1 };

        // Warm up, so JIT does not land on whichever runs first.
        Hasher(cheap).Hash("warmup");
        Hasher(dear).Hash("warmup");

        var cheapMs = Time(() => Hasher(cheap).Hash("password"));
        var dearMs = Time(() => Hasher(dear).Hash("password"));

        Assert.True(
            dearMs > cheapMs,
            $"Heavier parameters should cost more, but cheap={cheapMs}ms dear={dearMs}ms.");
    }

    [Fact]
    public void A_hash_at_the_configured_settings_does_not_need_rehashing()
    {
        var hasher = Hasher();

        Assert.False(hasher.NeedsRehash(hasher.Hash("admin123!")));
    }

    [Fact]
    public void A_hash_at_the_old_settings_is_flagged_for_rehash()
    {
        // This is what makes the change reach existing accounts. Without it the
        // seeded admin would keep paying the old cost on every sign-in forever.
        var legacy = Hasher(new PasswordHashingOptions
        {
            MemoryKb = 65536,
            Iterations = 3,
            Parallelism = 1
        }).Hash("admin123!");

        Assert.True(Hasher().NeedsRehash(legacy));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2id$v=19$garbage$salt$digest")]
    public void An_unreadable_hash_is_flagged_for_rehash(string hash)
    {
        // Better to replace something unrecognised at the next sign-in than to
        // leave it sitting in the users table.
        Assert.True(Hasher().NeedsRehash(hash));
    }

    [Fact]
    public void Rehash_detection_looks_at_every_work_factor()
    {
        var baseline = Hasher().Hash("admin123!");

        Assert.True(Hasher(new PasswordHashingOptions { MemoryKb = 32768, Iterations = 2, Parallelism = 1 })
            .NeedsRehash(baseline));
        Assert.True(Hasher(new PasswordHashingOptions { MemoryKb = 19456, Iterations = 3, Parallelism = 1 })
            .NeedsRehash(baseline));
        Assert.True(Hasher(new PasswordHashingOptions { MemoryKb = 19456, Iterations = 2, Parallelism = 2 })
            .NeedsRehash(baseline));
    }

    private static double Time(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }
}
