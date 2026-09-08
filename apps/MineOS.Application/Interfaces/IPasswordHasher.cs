namespace MineOS.Application.Interfaces;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);

    /// <summary>
    /// Whether a stored hash was made with different work factors than the ones
    /// now configured.
    ///
    /// Argon2 keeps its parameters inside the hash, so an old hash goes on
    /// verifying at its original cost forever. Without re-hashing, lowering the
    /// cost never reaches existing accounts — and, more importantly, raising it
    /// never protects them either.
    /// </summary>
    bool NeedsRehash(string hash);
}
