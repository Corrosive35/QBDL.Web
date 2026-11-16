using System.Security.Cryptography;
using System.Text;

namespace QBDL.Web.Services;

/// <summary>
/// Étranglement des tentatives de déverrouillage, au niveau de l'application.
/// Le compteur vivait auparavant dans AppGuardService, un service scoped : il était donc remis à
/// zéro par le simple fait d'ouvrir un nouvel onglet, ce qui rendait la limite décorative.
/// </summary>
/// <remarks>
/// Un seul seau, pas un seau par adresse : derrière un reverse proxy sans ForwardedHeaders, toutes
/// les requêtes portent l'adresse du proxy. Découper par IP donnerait exactement le même
/// comportement, avec l'illusion d'une granularité en plus.
/// </remarks>
public sealed class AppAccessGate
{
    /// <summary>Type du claim qui fige, dans le cookie, le mot de passe pour lequel il a été émis.</summary>
    public const string StampClaim = "qbdlx:stamp";

    private const int MaxAttempts = 5;

    // Palier suivant à chaque série d'échecs : réessayer en boucle coûte de plus en plus cher,
    // là où un délai fixe se contente d'imposer une pause avant de repartir au même rythme.
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    };

    private readonly object _lock = new();
    private int _failures;
    private int _level;
    private DateTime _lockedUntil = DateTime.MinValue;

    public bool IsLockedOut
    {
        get { lock (_lock) return DateTime.UtcNow < _lockedUntil; }
    }

    public int RemainingSeconds
    {
        get
        {
            lock (_lock)
            {
                var remaining = _lockedUntil - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? (int)remaining.TotalSeconds + 1 : 0;
            }
        }
    }

    public void RegisterFailure()
    {
        lock (_lock)
        {
            if (++_failures < MaxAttempts) return;
            _failures = 0;
            _lockedUntil = DateTime.UtcNow + Backoff[Math.Min(_level, Backoff.Length - 1)];
            _level++;
        }
    }

    /// <summary>Un déverrouillage réussi ramène le palier à zéro : les échecs d'hier ne pénalisent
    /// pas quelqu'un qui connaît le mot de passe.</summary>
    public void RegisterSuccess()
    {
        lock (_lock)
        {
            _failures = 0;
            _level = 0;
            _lockedUntil = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Empreinte du hachage d'un compte, embarquée dans le cookie puis revalidée à chaque requête.
    /// Changer le mot de passe d'un compte change l'empreinte, ce qui invalide d'un coup les cookies
    /// émis pour l'ancien sur tous les appareils de ce compte — sans quoi un appareil connecté le
    /// resterait indéfiniment après un changement de mot de passe.
    /// </summary>
    public static string StampFor(string? passwordHash) =>
        string.IsNullOrEmpty(passwordHash)
            ? "none"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)))[..16];
}
