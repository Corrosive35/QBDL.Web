using System.Globalization;
using System.Security.Claims;
using Newtonsoft.Json;

namespace QBDL.Web.Services;

/// <summary>
/// Traduction de l'interface. Les dictionnaires sont chargés une fois au démarrage depuis
/// wwwroot/languages/{fr,en}.json, d'où le singleton.
/// </summary>
/// <remarks>
/// La langue est propre à chaque utilisateur et ne vit pas ici : le middleware de localisation la
/// fixe pour chaque requête (<see cref="ResolveLanguage"/>), et un circuit Blazor conserve la culture
/// de la connexion qui l'a ouvert. <see cref="Current"/> n'a donc qu'à la lire. Le worker de
/// téléchargement, qui tourne hors de toute requête, passe par <see cref="In"/>.
/// </remarks>
public class LocalizationService
{
    public const string French = "fr";
    public const string English = "en";

    /// <summary>Langue choisie par un visiteur pas encore connecté, et reprise de celle du compte à la
    /// connexion pour que l'écran de connexion suivant parle la même langue.</summary>
    public const string CookieName = "qbdlx_lang";

    private readonly ILogger<LocalizationService> _logger;
    private readonly Dictionary<string, Dictionary<string, string>> _catalogs = new();

    public LocalizationService(IWebHostEnvironment env, ILogger<LocalizationService> logger)
    {
        _logger = logger;

        var dir = Path.Combine(env.WebRootPath ?? "wwwroot", "languages");
        _catalogs[French] = LoadCatalog(Path.Combine(dir, "fr.json"));
        _catalogs[English] = LoadCatalog(Path.Combine(dir, "en.json"));

        WarnAboutMissingKeys();
    }

    /// <summary>Langue de la requête ou du circuit en cours. Le français est le repli.</summary>
    public string Current => Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    public string this[string key] => Lookup(Current, key);

    /// <summary>Chaîne à trous : la valeur traduite est un modèle <c>string.Format</c> ({0}, {1}...).</summary>
    public string T(string key, params object?[] args) => Format(Current, CultureInfo.CurrentCulture, key, args);

    /// <summary>
    /// Pluriel simple à deux formes, via les clés <c>{key}.one</c> et <c>{key}.other</c>. Le nombre
    /// est injecté en {0}. Les règles diffèrent : le français met 0 au singulier (« 0 piste »),
    /// l'anglais au pluriel (« 0 tracks »).
    /// </summary>
    public string P(string key, int count) => Plural(Current, CultureInfo.CurrentCulture, key, count);

    /// <summary>
    /// Traducteur lié à une langue donnée, indépendant de la culture du thread. Pour le worker de
    /// téléchargement : il écrit le journal de chaque utilisateur dans la langue de celui-ci, quelle que
    /// soit la requête qui l'a démarré.
    /// </summary>
    public Translator In(string? lang)
    {
        var normalized = Normalize(lang);
        return new Translator(this, normalized, CultureFor(normalized));
    }

    // ─── Résolution de la langue d'une requête ───

    public static string Normalize(string? lang) => lang == English ? English : French;

    public static string CultureNameFor(string lang) => lang == English ? "en-US" : "fr-FR";

    /// <summary>
    /// Ordre de priorité : la langue enregistrée sur le compte connecté, puis le cookie d'un visiteur
    /// qui a choisi sur l'écran de connexion, puis l'en-tête Accept-Language du navigateur.
    /// </summary>
    public static string ResolveLanguage(HttpContext ctx, UserStore users)
    {
        var account = users.Find(ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        if (account?.UiLanguage is French or English) return account.UiLanguage;

        var cookie = ctx.Request.Cookies[CookieName];
        if (cookie is French or English) return cookie;

        return FromAcceptLanguage(ctx.Request.Headers["Accept-Language"].ToString());
    }

    /// <summary>
    /// Anglais si la première langue du navigateur est anglophone, français dans tous les autres cas.
    /// Fait à la main plutôt que par AcceptLanguageHeaderRequestCultureProvider : celui-ci exige une
    /// culture exactement prise en charge, et un navigateur réglé sur en-GB serait servi en français.
    /// </summary>
    public static string FromAcceptLanguage(string? acceptLanguage)
    {
        var first = (acceptLanguage ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Split(';')[0]
            .Trim();

        return first != null && first.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? English : French;
    }

    public static void WriteCookie(HttpResponse response, string lang, bool secure)
    {
        response.Cookies.Append(CookieName, Normalize(lang), new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = secure,
            Expires = DateTimeOffset.UtcNow.AddYears(1),
        });
    }

    // ─── Interne ───

    private string Format(string lang, IFormatProvider culture, string key, object?[] args)
    {
        var format = Lookup(lang, key);
        try
        {
            return string.Format(culture, format, args);
        }
        catch (FormatException)
        {
            // Un modèle mal formé ne doit jamais faire tomber un rendu.
            _logger.LogWarning("Modèle de traduction invalide pour la clé '{Key}' : {Format}", key, format);
            return format;
        }
    }

    private string Plural(string lang, IFormatProvider culture, string key, int count)
    {
        var singular = lang == French ? count <= 1 : count == 1;
        return Format(lang, culture, $"{key}.{(singular ? "one" : "other")}", new object?[] { count });
    }

    private string Lookup(string lang, string key)
    {
        // Repli en cascade : langue demandée -> français -> la clé elle-même (visible à l'écran,
        // donc immédiatement repérable en cas d'oubli).
        if (_catalogs.TryGetValue(lang, out var catalog) && catalog.TryGetValue(key, out var value))
            return value;
        if (_catalogs.TryGetValue(French, out var fr) && fr.TryGetValue(key, out var frValue))
            return frValue;
        return key;
    }

    /// <summary>
    /// L'image installe ICU, les cultures nommées existent donc. Le repli invariant ne sert que si
    /// l'application tourne en globalization-invariant mode : les traductions viennent des fichiers
    /// JSON et n'en souffrent pas, seuls les formats de nombres et de dates du journal changent.
    /// </summary>
    private static CultureInfo CultureFor(string lang)
    {
        try { return CultureInfo.GetCultureInfo(CultureNameFor(lang)); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    private Dictionary<string, string> LoadCatalog(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _logger.LogError("Fichier de traduction introuvable : {Path}", path);
                return new Dictionary<string, string>();
            }

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Impossible de charger le fichier de traduction {Path}", path);
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Filet de sécurité : sans tests ni build local, un déséquilibre entre les deux catalogues ne
    /// se verrait qu'à l'écran. On le signale au démarrage.
    /// </summary>
    private void WarnAboutMissingKeys()
    {
        var fr = _catalogs[French].Keys.ToHashSet();
        var en = _catalogs[English].Keys.ToHashSet();

        var missingInEn = fr.Except(en).OrderBy(k => k).ToList();
        var missingInFr = en.Except(fr).OrderBy(k => k).ToList();

        if (missingInEn.Count > 0)
            _logger.LogWarning("Traductions manquantes en anglais ({Count}) : {Keys}",
                missingInEn.Count, string.Join(", ", missingInEn));
        if (missingInFr.Count > 0)
            _logger.LogWarning("Traductions manquantes en français ({Count}) : {Keys}",
                missingInFr.Count, string.Join(", ", missingInFr));
    }

    /// <summary>Traductions dans une langue fixée à la construction. Voir <see cref="In"/>.</summary>
    public sealed class Translator
    {
        private readonly LocalizationService _owner;
        private readonly CultureInfo _culture;

        internal Translator(LocalizationService owner, string lang, CultureInfo culture)
        {
            _owner = owner;
            Language = lang;
            _culture = culture;
        }

        public string Language { get; }

        public CultureInfo Culture => _culture;

        public string this[string key] => _owner.Lookup(Language, key);

        public string T(string key, params object?[] args) => _owner.Format(Language, _culture, key, args);

        public string P(string key, int count) => _owner.Plural(Language, _culture, key, count);
    }
}
