using System.Security.Cryptography;
using System.Text;
using ArkansasServe.Functions.Models;

namespace ArkansasServe.Functions.Services;

/// <summary>
/// Mints and redeems a guardian's one-time access link (#20).
///
/// A guardian has no account, so this link IS their authentication. Owner decision:
/// SINGLE-USE, SEVEN DAYS, re-requestable. Long enough for a parent who checks email weekly,
/// short enough that a forwarded message goes stale, and spent the moment it is used.
///
/// ONLY THE HASH IS STORED. The raw token exists once, in the response that carries it into an
/// email, and is never written to Cosmos — the Users container is readable through the
/// SuperAdmin DB console, and a stored raw token would let anyone with read access act as that
/// guardian. Redemption hashes what it is given and looks that up.
///
/// This follows the check-in code pattern (#14: a stored random secret with an expiry) rather
/// than a signed JWT, for the same reason: nothing needs to be verified offline, and a value we
/// can revoke by deleting is easier to reason about than one we must wait out. The difference
/// is the hashing, because a check-in code admits you to a room and this one speaks for a child.
/// </summary>
public class GuardianLinkService
{
    // Owner decision. Not a constant to be tuned lightly: shortening it strands parents mid-flow
    // and lengthening it widens the window in which a forwarded email is a live credential.
    public const int LinkLifetimeDays = 7;

    /// <summary>
    /// A URL-safe 256-bit token. Deliberately NOT the check-in alphabet: that one is short and
    /// human-legible because someone reads it off a poster. This is clicked, never typed, so it
    /// should be long and unguessable instead.
    /// </summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static string HashToken(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    /// <summary>
    /// Issues a fresh link, REPLACING any previous one — a guardian has at most one live link,
    /// so re-requesting invalidates the earlier email rather than leaving two doors open.
    /// Returns the raw token; the caller is the only thing that will ever see it.
    /// </summary>
    public static string Issue(Guardian guardian, string? reason, DateTime now)
    {
        var token = NewToken();
        guardian.MagicLink = new MagicLinkState
        {
            TokenHash = HashToken(token),
            IssuedAt = now,
            ExpiresAt = now.AddDays(LinkLifetimeDays),
            ConsumedAt = null,
            Reason = reason,
        };
        return token;
    }

    /// <summary>
    /// How long a redeemed link's working session lasts. Long enough to read the wording and
    /// decide, short enough that a shared screen or an abandoned tab is not a standing
    /// credential. This is NOT a login and must not drift towards one.
    /// </summary>
    public const int SessionLifetimeMinutes = 30;

    /// <summary>
    /// Mints the session that redemption hands back, replacing any previous one. Returns the raw
    /// token; like the link token, only its hash is persisted.
    /// </summary>
    public static string IssueSession(Guardian guardian, DateTime now)
    {
        var token = NewToken();
        guardian.Session = new GuardianSessionState
        {
            TokenHash = HashToken(token),
            IssuedAt = now,
            ExpiresAt = now.AddMinutes(SessionLifetimeMinutes),
        };
        return token;
    }

    /// <summary>
    /// Why a presented token was refused. The CALLER decides how much of this to tell the
    /// browser — "expired" and "already used" are safe and helpful to a parent, but they are
    /// only ever reported for a token that genuinely matched a record.
    /// </summary>
    public enum RedeemResult { Ok, NotFound, Expired, AlreadyUsed }

    /// <summary>
    /// Validates the link state and, on success, marks it consumed IN MEMORY — the caller must
    /// persist the guardian for single-use to actually hold. Kept separate so the write and its
    /// concurrency handling stay with the caller rather than hidden in here.
    /// </summary>
    public static RedeemResult Redeem(Guardian? guardian, DateTime now)
    {
        var link = guardian?.MagicLink;
        if (guardian == null || link == null) return RedeemResult.NotFound;
        if (link.ConsumedAt != null) return RedeemResult.AlreadyUsed;
        if (link.ExpiresAt <= now) return RedeemResult.Expired;

        link.ConsumedAt = now;
        return RedeemResult.Ok;
    }
}
