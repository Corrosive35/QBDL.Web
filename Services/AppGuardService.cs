using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Authorization;
using QBDL.Web.Models;

namespace QBDL.Web.Services;

/// <summary>
/// Service scoped (une instance par circuit Blazor / onglet) : l'utilisateur connecté sur ce circuit.
/// </summary>
/// <remarks>
/// La connexion elle-même ne passe pas par ici mais par POST /auth/unlock, qui dépose un cookie
/// persistant : sans lui, l'état vivait dans le circuit et le moindre rechargement redemandait le
/// mot de passe — intenable sur mobile, où le système gèle et tue les WebView.
/// </remarks>
public class AppGuardService
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    private readonly AppAccessGate _gate;
    private readonly AuthenticationStateProvider _auth;
    private readonly UserStore _users;
    private string? _userId;

    public AppGuardService(AppAccessGate gate, AuthenticationStateProvider auth, UserStore users)
    {
        _gate = gate;
        _auth = auth;
        _users = users;
    }

    /// <summary>
    /// Identifiant du compte connecté, "" s'il n'y en a pas.
    ///
    /// Synchrone à dessein. Le ClaimsPrincipal est déposé dans le AuthenticationStateProvider
    /// avant le premier rendu : par le tag helper &lt;component&gt; au prérendu, puis par la
    /// CircuitFactory au démarrage du circuit, à partir de l'utilisateur du handshake WebSocket —
    /// qui porte lui aussi le cookie. La tâche est donc toujours déjà terminée, ce qui évite de
    /// rendre un premier état « verrouillé » avant de se raviser.
    /// </summary>
    public string UserId
    {
        get
        {
            if (_userId != null) return _userId;

            var state = _auth.GetAuthenticationStateAsync();
            if (state.IsCompletedSuccessfully && state.Result.User.Identity?.IsAuthenticated == true)
            {
                // Mémoïsé : le circuit reste ouvert jusqu'au prochain chargement de page, où le
                // cookie sera de nouveau confronté à l'empreinte du mot de passe courant.
                _userId = state.Result.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            }

            return _userId ?? "";
        }
    }

    /// <summary>
    /// Relu dans UserStore à chaque accès, jamais mémoïsé : un compte supprimé par l'administrateur
    /// verrouille ainsi son circuit dès le rendu suivant, et un rôle retiré cesse aussitôt de valoir.
    /// </summary>
    public UserAccount? Account => _users.Find(UserId);

    public bool IsUnlocked => Account != null;

    public bool IsAdmin => Account?.IsAdmin == true;

    /// <summary>Réglages de téléchargement du compte connecté. Le repli ne sert que dans la fenêtre où
    /// un compte vient d'être supprimé et où le layout n'a pas encore redessiné l'écran de connexion.</summary>
    public UserSettings Settings => Account?.Settings ?? new UserSettings();

    public bool IsLockedOut => _gate.IsLockedOut;
    public int RemainingSeconds => _gate.RemainingSeconds;

    /// <summary>
    /// Hash password with PBKDF2 + random salt.
    /// Returns base64 string: salt(16) + hash(32).
    /// </summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashSize);
        var result = new byte[SaltSize + HashSize];
        salt.CopyTo(result, 0);
        hash.CopyTo(result, SaltSize);
        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Reconnaît un hachage SHA256 hexadécimal issu d'une version antérieure : sans sel et sur un
    /// seul tour, donc à la portée d'une table précalculée si settings.json venait à fuir.
    /// </summary>
    /// <remarks>
    /// Sert à le remplacer par du PBKDF2 au prochain déverrouillage réussi — le seul instant où
    /// l'application détient le mot de passe en clair. La migration elle-même a lieu dans le
    /// gestionnaire POST /auth/unlock. Un tel hachage ne peut plus venir que du mot de passe d'accès
    /// 1.6, repris tel quel par le compte « admin » à la migration vers les comptes.
    /// </remarks>
    public static bool IsLegacyHash(string storedHash) =>
        storedHash.Length == 64 && storedHash.All(c => "0123456789abcdef".Contains(c));

    /// <summary>
    /// Verify password against stored PBKDF2 hash.
    /// Also supports legacy SHA256 hex hashes for migration.
    /// </summary>
    public static bool VerifyPassword(string password, string storedHash)
    {
        if (IsLegacyHash(storedHash))
        {
            var sha = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
            // Temps constant, comme sur le chemin PBKDF2 ci-dessous : un == de chaînes s'arrête au
            // premier caractère qui diffère et renseigne donc, par sa durée, sur la longueur du
            // préfixe correct.
            return CryptographicOperations.FixedTimeEquals(sha, Convert.FromHexString(storedHash));
        }

        try
        {
            var decoded = Convert.FromBase64String(storedHash);
            if (decoded.Length != SaltSize + HashSize) return false;

            var salt = decoded[..SaltSize];
            var storedHashBytes = decoded[SaltSize..];
            var computedHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashSize);

            return CryptographicOperations.FixedTimeEquals(storedHashBytes, computedHash);
        }
        catch
        {
            return false;
        }
    }
}
