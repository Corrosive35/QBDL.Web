using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using QBDL.Web.Models;

namespace QBDL.Web.Services;

/// <summary>
/// Connexion du compte Qobuz de l'instance : l'enregistrement commun à ses deux voies (jeton collé
/// sur la page de connexion, connexion Qobuz) et le déroulé de la connexion Qobuz.
/// </summary>
/// <remarks>
/// <para>
/// La connexion Qobuz est celle du lecteur web (OAuth). L'administrateur est envoyé sur la page de
/// connexion de qobuz.com et s'y connecte lui-même, reCAPTCHA compris : QBDL ne voit jamais le mot
/// de passe. Qobuz le renvoie ensuite sur <see cref="CallbackPath"/> avec un code, échangé ici
/// contre un jeton. Elle remplace la connexion par e-mail et mot de passe, que Qobuz refuse aux
/// applications tierces depuis avril 2026 environ.
/// </para>
/// <para>
/// Le retour n'est pas autorisé par le cookie d'accès. Dans l'application Android, le WebView
/// n'affiche que le serveur et ouvre la page de Qobuz dans le navigateur externe, qui n'a pas ce
/// cookie. C'est donc le state qui l'autorise : aléatoire, à usage unique, valable dix minutes, né
/// d'un administrateur connecté. Il voyage dans l'adresse de retour, que Qobuz restitue telle quelle.
/// Les connexions en cours ne vivent qu'en mémoire : un redémarrage les annule, ce qui est voulu.
/// </para>
/// </remarks>
public sealed class QobuzSignInService
{
    /// <summary>Route de retour, déclarée dans Program.cs.</summary>
    public const string CallbackPath = "/auth/qobuz/callback";

    // Page de connexion de Qobuz, telle que l'ouvre le lecteur web.
    private const string SignInUrl = "https://www.qobuz.com/signin/oauth";

    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private readonly QobuzApiService _api;
    private readonly SettingsService _settings;
    private readonly UserStore _users;
    private readonly ILogger<QobuzSignInService> _logger;
    private readonly ConcurrentDictionary<string, PendingSignIn> _pending = new();

    public QobuzSignInService(QobuzApiService api, SettingsService settings, UserStore users,
        ILogger<QobuzSignInService> logger)
    {
        _api = api;
        _settings = settings;
        _users = users;
        _logger = logger;
    }

    /// <summary>Issue du retour de la connexion Qobuz, traduite en message par la page de retour.</summary>
    public enum Outcome
    {
        Connected,
        /// <summary>State inconnu, expiré ou déjà utilisé.</summary>
        InvalidState,
        /// <summary>Le compte qui a lancé la connexion n'est plus administrateur.</summary>
        NotAdmin,
        /// <summary>Retour sans code, ou code ou jeton refusé par Qobuz.</summary>
        Refused,
        Unreachable,
        /// <summary>Compte reconnu, mais app_secret introuvable dans le lecteur web.</summary>
        AppCredentials,
    }

    /// <summary>
    /// Issue du retour et, en cas de succès, nom d'affichage du compte connecté (jamais l'e-mail).
    /// qobuz.com redonne sans rien demander le compte déjà ouvert dans le navigateur : le nom permet
    /// de vérifier d'un coup d'œil qu'un changement de compte a bien eu lieu.
    /// </summary>
    public sealed record SignInResult(Outcome Outcome, string? AccountName = null);

    /// <summary>
    /// Levé après chaque connexion enregistrée, quelle qu'en soit la voie. Une page de connexion
    /// restée ouverte s'en sert pour se recharger : celle du WebView, par exemple, pendant que le
    /// navigateur externe termine la connexion Qobuz.
    /// </summary>
    public event Action? AccountConnected;

    /// <summary>
    /// Panne réseau : aucune réponse HTTP (DNS, connexion, TLS) ou délai dépassé. Une
    /// HttpRequestException qui porte un code HTTP vient au contraire d'une réponse de Qobuz, et ne
    /// doit pas s'afficher comme « serveurs injoignables ».
    /// </summary>
    public static bool IsNetworkFailure(Exception ex) =>
        ex is HttpRequestException { StatusCode: null } or TaskCanceledException;

    /// <summary>
    /// Enregistre une connexion réussie : identifiants en service et dans settings.json, fiche du
    /// compte. Seul chemin d'enregistrement, pour que la connexion par jeton et la connexion Qobuz
    /// ne puissent pas diverger.
    /// </summary>
    public void Apply(string appId, string appSecret, QobuzLoginResponse result, bool viaToken)
    {
        var token = result.UserAuthToken!;
        _api.SetCredentials(appId, appSecret, token);

        _settings.Update(s =>
        {
            s.AppId = appId;
            s.AppSecret = appSecret;
            s.UserAuthToken = token;
            // Plus lu depuis la disparition de la connexion par e-mail : une adresse laissée par
            // une version précédente n'a plus de raison de rester dans settings.json.
            s.Email = "";
            // Voie qui a servi, pour information : la page de connexion montre désormais les deux
            // cadres et ne la lit plus.
            s.UseTokenLogin = viaToken;
            // Identité et état de l'abonnement : même code que la revalidation au démarrage.
            s.ApplyAccountStatus(result.User);
        });

        // Un abonné défaillant ne doit ni empêcher les autres d'être prévenus, ni faire échouer
        // une connexion déjà enregistrée.
        foreach (var handler in AccountConnected?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { _logger.LogWarning("AccountConnected handler failed: {Reason}", ex.Message); }
        }
    }

    /// <summary>
    /// Prépare une connexion Qobuz pour l'administrateur <paramref name="adminId"/> et renvoie
    /// l'adresse de la page de connexion de Qobuz.
    /// </summary>
    /// <param name="baseUri">
    /// NavigationManager.BaseUri du circuit, c'est-à-dire l'adresse que le navigateur affiche
    /// réellement : https derrière un proxy inverse, adresse VPN ou locale. Qobuz renvoie
    /// l'utilisateur sur son origine, sans réglage à remplir. Les en-têtes X-Forwarded-* ne sont
    /// pas lus, Program.cs explique pourquoi.
    /// </param>
    public async Task<string> BeginAsync(string adminId, string baseUri)
    {
        if (_users.Find(adminId)?.IsAdmin != true)
            throw new InvalidOperationException("Only an administrator can connect the Qobuz account");

        var (appId, privateKey) = await _api.GetOAuthKeysAsync();

        PurgeExpired();
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _pending[state] = new PendingSignIn
        {
            AdminId = adminId,
            AppId = appId,
            PrivateKey = privateKey,
            ExpiresAt = DateTime.UtcNow + StateLifetime,
        };

        // Base64url : le state se glisse tel quel dans l'adresse de retour.
        var origin = new Uri(baseUri).GetLeftPart(UriPartial.Authority);
        var redirect = $"{origin}{CallbackPath}?state={state}";
        _logger.LogInformation("Qobuz sign-in started, return expected on {Origin}", origin);

        return $"{SignInUrl}?ext_app_id={appId}&redirect_url={Uri.EscapeDataString(redirect)}";
    }

    /// <summary>
    /// Termine une connexion Qobuz au retour de qobuz.com : échange du code, fiche du compte,
    /// app_secret, puis le même enregistrement que la connexion par jeton (<see cref="Apply"/>).
    /// Ni le code, ni le state, ni la clé privée, ni le jeton ne sont journalisés.
    /// </summary>
    public async Task<SignInResult> CompleteAsync(string? state, string? code)
    {
        // Retiré avant toute vérification : un state ne sert qu'une fois, échec compris.
        if (string.IsNullOrEmpty(state) || !_pending.TryRemove(state, out var pending)
            || pending.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogWarning("Qobuz sign-in return rejected: unknown, expired or already used state");
            return new(Outcome.InvalidState);
        }

        // Rôle relu au retour : un compte rétrogradé ou supprimé entre-temps ne termine pas la
        // connexion qu'il avait lancée.
        if (_users.Find(pending.AdminId)?.IsAdmin != true)
        {
            _logger.LogWarning("Qobuz sign-in return rejected: the account that started it is no longer an administrator");
            return new(Outcome.NotAdmin);
        }

        // Pas de code : connexion abandonnée ou refusée sur qobuz.com.
        if (string.IsNullOrEmpty(code))
        {
            _logger.LogWarning("Qobuz sign-in returned without an authorization code");
            return new(Outcome.Refused);
        }

        string token;
        QobuzUserInfo? user;
        try
        {
            token = await _api.ExchangeOAuthCodeAsync(pending.AppId, pending.PrivateKey, code);
            // La fiche du compte valide le jeton et renseigne l'abonnement, comme pour un jeton
            // collé à la main.
            user = await _api.GetUserInfoAsync(pending.AppId, token);
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            _logger.LogWarning("Qobuz sign-in failed, servers unreachable: {Type}: {Reason}", ex.GetType().Name, ex.Message);
            return new(Outcome.Unreachable);
        }
        catch (Exception ex)
        {
            // ex.Message : code et message de Qobuz, jamais le code de retour ni le jeton.
            _logger.LogWarning("Qobuz sign-in failed: {Type}: {Reason}", ex.GetType().Name, ex.Message);
            return new(Outcome.Refused);
        }

        string appSecret;
        try
        {
            appSecret = await _api.GetAppSecretAsync(pending.AppId, token);
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            _logger.LogWarning("Qobuz sign-in failed, servers unreachable: {Type}: {Reason}", ex.GetType().Name, ex.Message);
            return new(Outcome.Unreachable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Automatic app_secret resolution failed: {Type}: {Reason}", ex.GetType().Name, ex.Message);
            return new(Outcome.AppCredentials);
        }

        Apply(pending.AppId, appSecret, new QobuzLoginResponse { UserAuthToken = token, User = user }, viaToken: false);
        _logger.LogInformation("Qobuz account connected through Qobuz sign-in");
        return new(Outcome.Connected, user?.DisplayName);
    }

    // Les connexions abandonnées ne s'accumulent pas : purgées à chaque nouveau départ.
    private void PurgeExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _pending)
        {
            if (entry.Value.ExpiresAt < now)
                _pending.TryRemove(entry.Key, out _);
        }
    }

    // Classe et non record : le ToString généré d'un record afficherait la clé privée si
    // l'objet finissait un jour dans un journal.
    private sealed class PendingSignIn
    {
        public required string AdminId { get; init; }
        public required string AppId { get; init; }
        public required string PrivateKey { get; init; }
        public required DateTime ExpiresAt { get; init; }
    }
}
