using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;
using MineOS.Application.Options;
using MineOS.Infrastructure.Persistence;

namespace MineOS.Infrastructure.Services;

public sealed class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly JwtOptions _jwtOptions;

    /// <summary>
    /// A genuine Argon2id hash of a value nobody knows, used to spend the same
    /// time on a login for an account that does not exist as on one that does.
    ///
    /// Produced by the configured hasher rather than written out by hand, for two
    /// reasons: a literal that failed to parse would be rejected instantly and
    /// leave the timing gap wide open, and it must carry the same work factors as
    /// real hashes or the two paths would still differ. Built once, lazily —
    /// hashing per request would double the cost of every failed sign-in.
    /// </summary>
    private readonly Lazy<string> _dummyHash;

    public AuthService(
        AppDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IOptions<JwtOptions> jwtOptions)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _jwtOptions = jwtOptions.Value;
        _dummyHash = new Lazy<string>(() => passwordHasher.Hash(Guid.NewGuid().ToString()));
    }

    public async Task<LoginResultDto?> LoginAsync(LoginRequestDto request, CancellationToken cancellationToken)
    {
        var username = request.Username.Trim();
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), cancellationToken);

        if (user == null || !user.IsActive)
        {
            // Hash anyway, against a throwaway value, so a request for an unknown
            // account costs the same as one for a real account. Returning here
            // immediately made the two measurably different — a real username
            // took ~150 ms and a made-up one ~1 ms — which let anyone discover
            // which accounts exist just by timing the responses.
            _passwordHasher.Verify(request.Password, _dummyHash.Value);
            return null;
        }

        if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            return null;
        }

        // Upgrade the stored hash to the configured work factors. We have the
        // plaintext only here, at a successful sign-in, so this is the one moment
        // it can be done — otherwise every existing account keeps its original
        // cost forever, and a change to those settings reaches nobody.
        if (_passwordHasher.NeedsRehash(user.PasswordHash))
        {
            await RehashAsync(user.Id, request.Password, cancellationToken);
        }

        var token = _jwtTokenService.CreateToken(user);
        return new LoginResultDto(
            AccessToken: token,
            ExpiresInSeconds: _jwtOptions.ExpiresMinutes * 60,
            TokenType: "Bearer",
            Username: user.Username,
            Role: user.Role);
    }

    /// <summary>
    /// Replaces a user's stored hash with one built at the configured work
    /// factors.
    ///
    /// Failures are swallowed on purpose: the sign-in itself already succeeded,
    /// and refusing to let someone in because a background upgrade hit a locked
    /// database would be a far worse outcome than leaving the old hash in place
    /// to be retried next time.
    /// </summary>
    private async Task RehashAsync(int userId, string password, CancellationToken cancellationToken)
    {
        try
        {
            var tracked = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (tracked == null)
            {
                return;
            }

            tracked.PasswordHash = _passwordHasher.Hash(password);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Deliberately ignored — see the note above.
        }
    }
}
