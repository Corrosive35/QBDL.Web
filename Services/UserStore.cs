using System.Text.RegularExpressions;
using Newtonsoft.Json;
using QBDL.Web.Models;

namespace QBDL.Web.Services;

/// <summary>
/// Comptes de l'application, persistés dans users.json. Singleton : la file de téléchargement et la
/// validation du cookie y lisent à chaque requête.
/// </summary>
/// <remarks>
/// Les méthodes qui modifient un compte renvoient <c>null</c> en cas de succès, sinon la clé de
/// traduction de l'erreur — l'appelant n'a qu'à la passer à LocalizationService.
/// </remarks>
public sealed class UserStore
{
    public const string DefaultAdminName = "admin";
    public const string DownloadsRoot = "/downloads";

    private static readonly Regex UsernameRegex = new(@"^[A-Za-z0-9._-]{1,32}$", RegexOptions.Compiled);

    // Cible de la vérification quand l'identifiant saisi n'existe pas : sans elle, un identifiant
    // inconnu répondrait aussitôt et un identifiant valide après 100 000 tours de PBKDF2 — la durée
    // de la réponse suffirait à dresser la liste des comptes.
    private static readonly string DummyHash = AppGuardService.HashPassword(Guid.NewGuid().ToString("N"));

    private readonly object _lock = new();
    private readonly string _configPath;
    private readonly string _path;
    private readonly ILogger<UserStore> _logger;
    private UsersFile _file;

    private sealed class UsersFile
    {
        public List<UserAccount> Users { get; set; } = new();

        // Réglages d'une installation 1.6 qui n'avait pas de mot de passe : aucun compte ne peut être
        // créé d'office, ils attendent donc ici le premier administrateur. Les garder dans users.json,
        // et non en mémoire, leur fait passer un redémarrage survenu avant sa création.
        public BootstrapState? Bootstrap { get; set; }
    }

    private sealed class BootstrapState
    {
        public UserSettings Settings { get; set; } = new();
        public string UiLanguage { get; set; } = "";
    }

    public UserStore(SettingsService settings, ILogger<UserStore> logger)
    {
        _logger = logger;
        _configPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "/config";
        _path = Path.Combine(_configPath, "users.json");
        _file = File.Exists(_path) ? Load() : Migrate(settings);
    }

    // ─── Lecture ───

    public bool HasUsers
    {
        get { lock (_lock) return _file.Users.Count > 0; }
    }

    public IReadOnlyList<UserAccount> All()
    {
        lock (_lock) return _file.Users.ToList();
    }

    public UserAccount? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_lock) return _file.Users.FirstOrDefault(u => u.Id == id);
    }

    public UserAccount? FindByName(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var name = username.Trim();
        lock (_lock)
            return _file.Users.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Dossier où vivait la bibliothèque avant les comptes : celui du premier administrateur,
    /// ou celui qui attend sa création.</summary>
    public string LegacyDownloadLocation
    {
        get
        {
            lock (_lock)
                return _file.Users.FirstOrDefault(u => u.IsAdmin)?.Settings.DownloadLocation
                       ?? _file.Bootstrap?.Settings.DownloadLocation
                       ?? DownloadsRoot;
        }
    }

    public static string DefaultFolderFor(string username) => $"{DownloadsRoot}/{username.Trim()}";

    /// <summary>
    /// Renvoie le compte si le mot de passe correspond, null sinon — identifiant inconnu compris,
    /// et en un temps comparable dans les deux cas.
    /// </summary>
    public UserAccount? Authenticate(string? username, string password)
    {
        var user = FindByName(username);
        var ok = AppGuardService.VerifyPassword(password, user?.PasswordHash ?? DummyHash);
        return ok ? user : null;
    }

    // ─── Écriture ───

    /// <summary>Enregistre les modifications faites directement sur un compte (réglages liés par
    /// @bind dans les Paramètres).</summary>
    public void Save()
    {
        lock (_lock) SaveLocked();
    }

    public void Update(string id, Action<UserAccount> action)
    {
        lock (_lock)
        {
            var user = _file.Users.FirstOrDefault(u => u.Id == id);
            if (user == null) return;
            action(user);
            SaveLocked();
        }
    }

    /// <summary>
    /// Premier administrateur d'une instance qui n'a encore aucun compte. Refusé dès qu'un compte
    /// existe : la vérification et la création se font sous le même verrou, deux requêtes
    /// simultanées ne peuvent donc pas créer chacune le leur.
    /// </summary>
    public (UserAccount? User, string? Error) CreateFirstAdmin(string username, string password)
    {
        lock (_lock)
        {
            if (_file.Users.Count > 0) return (null, "setup.error.alreadyDone");

            var error = ValidateUsernameLocked(username, exceptId: null) ?? ValidatePassword(password);
            if (error != null) return (null, error);

            var user = new UserAccount
            {
                Username = username.Trim(),
                PasswordHash = AppGuardService.HashPassword(password),
                IsAdmin = true,
                UiLanguage = _file.Bootstrap?.UiLanguage ?? "",
                // L'ancienne bibliothèque reste où elle est : le premier administrateur en hérite.
                Settings = _file.Bootstrap?.Settings ?? new UserSettings(),
            };

            _file.Users.Add(user);
            _file.Bootstrap = null;
            SaveLocked();
            _logger.LogInformation("Compte administrateur « {User} » créé à l'installation", user.Username);
            return (user, null);
        }
    }

    public (UserAccount? User, string? Error) Create(string username, string password, bool isAdmin, string folder)
    {
        lock (_lock)
        {
            var error = ValidateUsernameLocked(username, exceptId: null) ?? ValidatePassword(password);
            if (error != null) return (null, error);

            error = NormalizeFolder(folder, out var normalized);
            if (error != null) return (null, error);

            var user = new UserAccount
            {
                Username = username.Trim(),
                PasswordHash = AppGuardService.HashPassword(password),
                IsAdmin = isAdmin,
                Settings = new UserSettings { DownloadLocation = normalized },
            };

            _file.Users.Add(user);
            SaveLocked();
            _logger.LogInformation("Compte « {User} » créé", user.Username);
            return (user, null);
        }
    }

    public string? Rename(string id, string username)
    {
        lock (_lock)
        {
            var user = _file.Users.FirstOrDefault(u => u.Id == id);
            if (user == null) return "users.error.notFound";

            var error = ValidateUsernameLocked(username, exceptId: id);
            if (error != null) return error;

            user.Username = username.Trim();
            SaveLocked();
            return null;
        }
    }

    public string? SetFolder(string id, string folder)
    {
        lock (_lock)
        {
            var user = _file.Users.FirstOrDefault(u => u.Id == id);
            if (user == null) return "users.error.notFound";

            var error = NormalizeFolder(folder, out var normalized);
            if (error != null) return error;

            user.Settings.DownloadLocation = normalized;
            SaveLocked();
            return null;
        }
    }

    /// <summary>Change le hachage, donc l'empreinte du cookie : tous les appareils de ce compte sont
    /// déconnectés à leur prochaine requête.</summary>
    public string? SetPassword(string id, string password)
    {
        var error = ValidatePassword(password);
        if (error != null) return error;

        // Hors verrou : 100 000 tours de PBKDF2 n'ont pas à bloquer les autres lectures.
        var hash = AppGuardService.HashPassword(password);
        lock (_lock)
        {
            var user = _file.Users.FirstOrDefault(u => u.Id == id);
            if (user == null) return "users.error.notFound";
            user.PasswordHash = hash;
            SaveLocked();
            return null;
        }
    }

    public string? SetAdmin(string id, bool isAdmin)
    {
        lock (_lock)
        {
            var user = _file.Users.FirstOrDefault(u => u.Id == id);
            if (user == null) return "users.error.notFound";

            // Sans administrateur, plus personne ne pourrait gérer les comptes ni le compte Qobuz.
            if (!isAdmin && user.IsAdmin && _file.Users.Count(u => u.IsAdmin) <= 1)
                return "users.error.lastAdmin";

            user.IsAdmin = isAdmin;
            SaveLocked();
            return null;
        }
    }

    /// <summary>Supprime le compte. Ses fichiers ne sont jamais touchés ; vider sa file et son état de
    /// recherche revient à l'appelant.</summary>
    public string? Delete(string actorId, string id)
    {
        lock (_lock)
        {
            if (actorId == id) return "users.error.self";

            var user = _file.Users.FirstOrDefault(u => u.Id == id);
            if (user == null) return "users.error.notFound";
            if (user.IsAdmin && _file.Users.Count(u => u.IsAdmin) <= 1) return "users.error.lastAdmin";

            _file.Users.Remove(user);
            SaveLocked();
            _logger.LogInformation("Compte « {User} » supprimé", user.Username);
            return null;
        }
    }

    // ─── Validation ───

    private string? ValidateUsernameLocked(string? username, string? exceptId)
    {
        var name = username?.Trim() ?? "";
        if (!UsernameRegex.IsMatch(name)) return "users.error.invalidName";

        var taken = _file.Users.Any(u => u.Id != exceptId
                                         && string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase));
        return taken ? "users.error.nameTaken" : null;
    }

    private static string? ValidatePassword(string? password) =>
        string.IsNullOrWhiteSpace(password) ? "users.error.emptyPassword" : null;

    /// <summary>
    /// Un dossier de téléchargement doit être absolu, et jamais / ni sous CONFIG_PATH : une piste
    /// nommée par un modèle pourrait sinon écraser users.json ou le trousseau Data Protection.
    /// </summary>
    public string? NormalizeFolder(string? folder, out string normalized)
    {
        normalized = "";
        var raw = folder?.Trim() ?? "";
        if (!raw.StartsWith('/')) return "users.error.folderNotAbsolute";

        string full;
        try { full = Path.GetFullPath(raw).TrimEnd('/'); }
        catch { return "users.error.folderInvalid"; }

        if (full.Length == 0) return "users.error.folderRoot";

        var config = Path.GetFullPath(_configPath).TrimEnd('/');
        if (full == config || full.StartsWith(config + "/", StringComparison.Ordinal))
            return "users.error.folderConfig";

        normalized = full;
        return null;
    }

    // ─── Persistance ───

    private UsersFile Load()
    {
        try
        {
            return JsonConvert.DeserializeObject<UsersFile>(File.ReadAllText(_path)) ?? new UsersFile();
        }
        catch (Exception ex)
        {
            // Surtout pas de repli sur une liste vide : l'écran « Créer le compte administrateur »
            // s'afficherait, et le premier venu prendrait la main sur l'instance. Mieux vaut un
            // conteneur qui refuse de démarrer, avec la cause dans les logs.
            _logger.LogCritical(ex, "users.json illisible ({Path}) : démarrage interrompu", _path);
            throw new InvalidOperationException($"users.json illisible : {_path}", ex);
        }
    }

    /// <summary>Écriture atomique : le fichier porte les hachages de tous les comptes, une écriture
    /// interrompue ne doit jamais le laisser tronqué.</summary>
    private void SaveLocked()
    {
        Directory.CreateDirectory(_configPath);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(_file, Formatting.Indented));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// Passage d'une installation 1.6 aux comptes. Ne tourne que si users.json n'existe pas : son
    /// existence sert de marqueur.
    /// </summary>
    private UsersFile Migrate(SettingsService settings)
    {
        var legacyPath = settings.FilePath;
        var prefs = new UserSettings();

        if (File.Exists(legacyPath))
        {
            try
            {
                var backup = legacyPath + ".pre-1.7.0.bak";
                if (!File.Exists(backup)) File.Copy(legacyPath, backup);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sauvegarde de settings.json impossible avant la migration");
            }

            // Les champs de téléchargement portent les mêmes noms dans UserSettings : relire le
            // fichier brut dans cette classe suffit. AppSettings, lui, ne les déclare plus.
            try
            {
                prefs = JsonConvert.DeserializeObject<UserSettings>(File.ReadAllText(legacyPath)) ?? new UserSettings();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Réglages 1.6 illisibles : valeurs par défaut pour le premier compte");
            }
        }

        var legacy = settings.Settings;
        var language = legacy.UiLanguage is "fr" or "en" ? legacy.UiLanguage : "";
        var file = new UsersFile();

        if (!string.IsNullOrEmpty(legacy.AppAccessPasswordHash))
        {
            // Même hachage que l'ancien mot de passe d'accès : rien à ressaisir. S'il date d'avant
            // PBKDF2, /auth/unlock le convertira au premier déverrouillage, comme en 1.6.
            file.Users.Add(new UserAccount
            {
                Username = DefaultAdminName,
                PasswordHash = legacy.AppAccessPasswordHash,
                IsAdmin = true,
                UiLanguage = language,
                Settings = prefs,
            });
            _logger.LogInformation(
                "Migration 1.7.0 : compte administrateur « {User} » créé à partir du mot de passe d'accès",
                DefaultAdminName);
        }
        else
        {
            file.Bootstrap = new BootstrapState { Settings = prefs, UiLanguage = language };
            _logger.LogInformation(
                "Migration 1.7.0 : aucun mot de passe d'accès, le premier administrateur sera créé à la première visite");
        }

        // users.json d'abord : c'est lui le marqueur. Si l'écriture de settings.json échouait ensuite,
        // la migration ne serait pas rejouée sur des champs déjà effacés.
        _file = file;
        SaveLocked();

        settings.Update(s =>
        {
            s.AppAccessPasswordHash = "";
            s.UiLanguage = "";
        });

        return file;
    }
}
