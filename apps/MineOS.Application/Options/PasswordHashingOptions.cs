namespace MineOS.Application.Options;

/// <summary>
/// Argon2id work factors for password hashing.
///
/// These are a deliberate cost: the whole point is to be slow enough that
/// guessing passwords is expensive. But the cost lands on every sign-in, and the
/// library defaults (64 MiB, 3 passes) take ~150 ms on a desktop and well over a
/// second on the single-board machines MineOS is often run on — where they also
/// mean 64 MiB allocated per concurrent attempt.
///
/// The defaults here are OWASP's recommended Argon2id profile, which is
/// considered equivalent in strength to the heavier settings while costing
/// roughly a third as much. Raise them on hardware that can afford it.
///
/// Changing these does not invalidate existing passwords: Argon2 stores the
/// parameters it used inside the hash, so old hashes keep verifying with the
/// settings they were created under, and only get the new cost when the password
/// is next changed.
/// </summary>
public sealed class PasswordHashingOptions
{
    /// <summary>Memory cost in kibibytes. OWASP's recommendation is 19 MiB.</summary>
    public int MemoryKb { get; set; } = 19456;

    /// <summary>Passes over memory.</summary>
    public int Iterations { get; set; } = 2;

    /// <summary>Lanes. Above 1 only helps when the host has cores to spare.</summary>
    public int Parallelism { get; set; } = 1;
}
