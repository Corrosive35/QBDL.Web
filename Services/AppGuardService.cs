using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Authorization;
using QBDL.Web.Models;

namespace QBDL.Web.Services;

/// <summary>
/// Service scoped (une instance par circuit Blazor) : l'utilisateur connecté sur ce circuit.
/// </summary>
/// <remarks>
/// La connexion passe par POST /auth/unlock, qui dépose un cookie persistant : un état gardé dans
/// le circuit redemanderait le mot de passe à chaque rechargement, intenable dans une WebView que
/// le système gèle et tue.
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
    /// Identifiant du compte connecté, "" s'il n'y en a pas. Synchrone à dessein : le
    /// ClaimsPrincipal est déposé avant le premier rendu (au prérendu, puis par la CircuitFactory à
    /// partir du handshake WebSocket, qui porte aussi le cookie). La tâche est donc toujours
    /// terminée, ce qui évite un premier rendu « verrouillé ».
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

    /// <summary>PBKDF2 avec sel aléatoire ; renvoie salt(16) + hash(32) en base64.</summary>
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
    /// Reconnaît un ancien hachage SHA256 hexadécimal, sans sel et sur un seul tour : celui du mot
    /// de passe d'accès 1.6, repris par le compte « admin ». POST /auth/unlock le remplace par du
    /// PBKDF2 au prochain déverrouillage réussi, seul instant où le mot de passe est connu en clair.
    /// </summary>
    public static bool IsLegacyHash(string storedHash) =>
        storedHash.Length == 64 && storedHash.All(c => "0123456789abcdef".Contains(c));

    /// <summary>Accepte aussi l'ancien hachage SHA256 (voir <see cref="IsLegacyHash"/>).</summary>
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
