using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using QBDL.Web.Models;

namespace QBDL.Web.Services;

public enum QueueItemStatus { Pending, Running, Completed, Failed, Cancelled }

public enum EnqueueResult { Added, Requeued, AlreadyQueued, InvalidUrl }

public class QueueItem
{
    /// <summary>Identité interne stable : sert de @key Blazor et de cible aux actions de l'UI.
    /// Ne participe jamais à l'unicité, qui repose sur <see cref="Url"/>.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Compte qui a ajouté l'élément : ses réglages, son dossier et sa langue servent au
    /// téléchargement, et lui seul le voit et peut agir dessus.</summary>
    public string OwnerId { get; init; } = "";

    /// <summary>Clé métier : URL canonique play.qobuz.com/{kind}/{refId}, sans requête ni fragment.
    /// C'est elle qui porte l'unicité — par propriétaire —, le grisage des boutons et la marque
    /// « déjà téléchargé ».</summary>
    public string Url { get; init; } = "";

    public string Kind { get; init; } = "";   // album | track | artist | playlist | label | user
    public string RefId { get; init; } = "";

    // Affichage. Renseignés par l'appelant quand il les a sous la main (page Recherche) ; repli sur
    // l'URL quand l'ajout vient du champ libre de la page Téléchargement.
    public string Label { get; set; } = "";
    public string? SubLabel { get; set; }
    public string? Thumbnail { get; set; }

    public QueueItemStatus Status { get; set; } = QueueItemStatus.Pending;
    public string? Error { get; set; }

    public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public bool IsFinished =>
        Status is QueueItemStatus.Completed or QueueItemStatus.Failed or QueueItemStatus.Cancelled;
}

public class DownloadProgress
{
    public int Percent { get; set; }
    public string Status { get; set; } = "";
    public string CurrentItem { get; set; } = "";
    public string Speed { get; set; } = "";

    /// <summary>Un élément de la file est en cours de traitement.</summary>
    public bool IsRunning { get; set; }

    /// <summary>La file a été drainée au moins une fois et est au repos.</summary>
    public bool IsComplete { get; set; }

    public QobuzAlbum? CurrentAlbum { get; set; }

    /// <summary>
    /// Piste en cours (base 0) et nombre de pistes, pendant le téléchargement des pistes d'un album
    /// ou d'une playlist : la page Téléchargement en tire un segment par piste. Steps vaut 0 dans
    /// tous les autres cas (discographie, favoris…), et la page affiche alors une simple barre.
    /// </summary>
    public int Step { get; set; }
    public int Steps { get; set; }

    /// <summary>Identifiant de l'élément en cours, pour surligner sa ligne sans comparer de libellé.</summary>
    public string? CurrentQueueItemId { get; set; }
}

/// <summary>Demande d'ajout à la file. Les champs d'affichage sont facultatifs : la page Recherche
/// les connaît, le champ libre de la page Téléchargement non.</summary>
public record QueueRequest(string Url, string? Label = null, string? SubLabel = null, string? Thumbnail = null);

/// <summary>
/// Le compte n'a pas (ou plus) les droits de streaming demandés. Qobuz ne renvoie pas d'erreur
/// dans ce cas : l'appel aboutit avec un extrait de 30 s en MP3, que l'ancien code téléchargeait
/// puis tentait d'étiqueter comme un FLAC — d'où des « FLAC stream not found at starting position »
/// à répétition, sans jamais nommer la vraie cause.
///
/// Cette exception traverse tous les filets qui absorbent les échecs de piste : réessayer n'a
/// aucun sens, descendre en qualité non plus, et les éléments suivants échoueraient à l'identique.
/// Elle remonte donc jusqu'au worker, qui arrête toutes les files : le compte Qobuz est partagé.
/// </summary>
public class SubscriptionRequiredException : Exception
{
    public SubscriptionRequiredException(string message) : base(message) { }
}

public class DownloadService
{
    private readonly QobuzApiService _api;
    private readonly SettingsService _settings;
    private readonly UserStore _users;
    private readonly TemplateService _templates;
    private readonly TaggingService _tagging;
    private readonly LocalizationService _loc;
    private readonly ILogger<DownloadService> _logger;

    // ─── État partagé ───
    //
    // Un verrou unique protège la file, les files par utilisateur (Lane : journal, worker) et les
    // deux index dérivés. Le service est Singleton et les workers tournent hors du circuit Blazor :
    // toute lecture destinée au rendu passe par un instantané (QueueSnapshot / LogSnapshot), jamais
    // par la collection vive, sinon l'énumération concurrente lève « Collection was modified ».
    private readonly object _gate = new();
    private readonly List<QueueItem> _queue = new();
    private readonly Dictionary<string, Lane> _lanes = new();

    // Index dérivés de _queue, pour que le grisage des boutons de la page Recherche ne soit pas
    // un balayage linéaire par bouton rendu. Clés « propriétaire|url » : deux utilisateurs peuvent
    // demander le même album, chacun dans son dossier.
    private readonly HashSet<string> _activeUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _doneUrls = new(StringComparer.OrdinalIgnoreCase);

    // Trois portées d'annulation, trois gestes distincts :
    //   _appCts           → l'application s'arrête (docker stop) : on abandonne tout, immédiatement
    //   Lane.ItemCts      → « ANNULER » : l'élément courant de cet utilisateur est abandonné
    //   Lane.SkipCurrent  → « PASSER » : saute l'album courant, reste dans le même élément
    private readonly CancellationTokenSource _appCts = new();
    private readonly CancellationToken _appCt;

    // Le compte n'a pas les droits de streaming : tant que ce drapeau est levé, aucune file ne prend
    // de nouvel élément. Les éléments restants échoueraient tous de la même façon, et les marquer en
    // échec en rafale effacerait la file sans que l'utilisateur ait eu le temps de lire la cause.
    // Global et non par file : le compte Qobuz est celui de tous. Protégé par _gate, levé par un
    // worker et abaissé par tout ajout ou « relancer » explicite.
    private bool _subscriptionBlocked;

    private const int LogMaxLines = 1200;
    private const int LogTrimLines = 400;

    // Progression d'un utilisateur qui n'a encore rien lancé. Jamais mutée.
    private static readonly DownloadProgress IdleProgress = new();

    /// <summary>Haute fréquence (pourcentage, vitesse) : étranglé à 200 ms par file, sauf appel
    /// forcé. Porte l'identifiant du propriétaire : chaque page ne se redessine que pour le sien,
    /// et non pour tous les transferts en cours sur l'instance.</summary>
    public event Action<string?>? OnProgressChanged;

    /// <summary>Basse fréquence (ajout, fin, réordonnancement) : jamais étranglé. C'est celui-là
    /// qu'écoutent la pastille du menu et le grisage des boutons de recherche — les abonner à
    /// OnProgressChanged les redessinerait à chaque bloc reçu. Identifiant du propriétaire, ou null
    /// pour un changement qui concerne tout le monde (levée du blocage d'abonnement).</summary>
    public event Action<string?>? OnQueueChanged;

    private static readonly Regex QobuzStoreLinkRegex = new(
        @"https://(?:.*?).qobuz.com/(?<region>.*?)/(?<type>.*?)/(?<name>.*?)/(?<id>.*?)$", RegexOptions.Compiled);
    private static readonly Regex QobuzLinkIdGrabRegex = new(
        @"https://(?:.*?).qobuz.com/(?<type>.*?)/(?<id>.*?)$", RegexOptions.Compiled);
    private static readonly Regex QobuzUrlRegex = new(
        @"^https?://(?!.*https?://)(?:[\w\-]+\.)?qobuz\.com/[^\s]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// La file d'un utilisateur. Porte tout ce qui était unique quand l'instance n'avait qu'une file :
    /// chaque utilisateur télécharge en parallèle des autres, avec sa progression, ses gestes
    /// « ANNULER »/« PASSER » et son journal. Au sein d'une file, un élément à la fois, dans l'ordre.
    /// </summary>
    private sealed class Lane
    {
        public Lane(string ownerId) => OwnerId = ownerId;

        public string OwnerId { get; }
        public DownloadProgress Progress { get; } = new();

        /// <summary>Sous _gate.</summary>
        public List<string> Log { get; } = new();

        /// <summary>Sous _gate. Non nul tant que le worker de cette file tourne : il démarre à la
        /// demande et se retire, sous ce même verrou, quand il ne trouve plus rien à faire.</summary>
        public Task? Worker;

        public CancellationTokenSource? ItemCts;
        public volatile bool SkipCurrent;
        public long LastProgressTick;
    }

    /// <summary>
    /// Contexte d'un élément en cours. Plusieurs éléments tournant en même temps — un par
    /// utilisateur —, il ne peut plus vivre dans des champs du service : il est passé à toute la
    /// chaîne de téléchargement.
    /// </summary>
    private sealed class ItemRun
    {
        public ItemRun(Lane lane, UserSettings settings, LocalizationService.Translator l, CancellationToken ct)
        {
            Lane = lane;
            Settings = settings;
            L = l;
            Ct = ct;
        }

        public Lane Lane { get; }
        public DownloadProgress Progress => Lane.Progress;

        /// <summary>Réglages du propriétaire. La référence vive, comme avant : une modification faite
        /// dans les Paramètres pendant un téléchargement s'applique aux pistes suivantes.</summary>
        public UserSettings Settings { get; }

        /// <summary>Traductions dans la langue du propriétaire, pour son journal.</summary>
        public LocalizationService.Translator L { get; }

        public CancellationToken Ct { get; }

        /// <summary>Distingue les fichiers temporaires de deux éléments qui tourneraient en même
        /// temps sur la même piste ou le même album.</summary>
        public string SessionId { get; } = Guid.NewGuid().ToString("N")[..8];
    }

    public DownloadService(QobuzApiService api, SettingsService settings, UserStore users, TemplateService templates, TaggingService tagging, LocalizationService loc, IHostApplicationLifetime lifetime, ILogger<DownloadService> logger)
    {
        _api = api;
        _settings = settings;
        _users = users;
        _templates = templates;
        _tagging = tagging;
        _loc = loc;
        _logger = logger;

        _appCt = _appCts.Token;
        // Sans cela, `docker stop` attendrait le délai de grâce complet pendant qu'un téléchargement
        // continue dans le vide.
        lifetime.ApplicationStopping.Register(() => { try { _appCts.Cancel(); } catch { } });

        MigrateTempFolder();
    }

    /// <summary>
    /// Migrate qbdlx-temp from /downloads to /config for existing installations.
    /// </summary>
    private void MigrateTempFolder()
    {
        try
        {
            // Les installations concernées sont antérieures aux comptes : leur dossier de
            // téléchargement est devenu celui du premier administrateur.
            var oldTempDir = Path.Combine(_users.LegacyDownloadLocation, "qbdlx-temp");
            var configPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "/config";
            var newTempDir = Path.Combine(configPath, "qbdlx-temp");

            if (Directory.Exists(oldTempDir) && oldTempDir != newTempDir)
            {
                if (Directory.Exists(newTempDir))
                    Directory.Delete(newTempDir, true);

                Directory.Move(oldTempDir, newTempDir);
                _logger.LogInformation("Migrated temp folder from {Old} to {New}", oldTempDir, newTempDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to migrate temp folder, it will be recreated in /config");
        }
    }

    // ─── Lecture pour l'interface ───

    /// <summary>Un élément de la file de cet utilisateur est en cours de traitement. Ne grise plus
    /// aucun bouton d'ajout : c'est tout l'intérêt de la file.</summary>
    public bool IsRunning(string ownerId) => ProgressFor(ownerId).IsRunning;

    /// <summary>Progression de la file de cet utilisateur. Les champs sont mutés par son worker :
    /// à relire à chaque rendu, comme avant.</summary>
    public DownloadProgress ProgressFor(string ownerId)
    {
        lock (_gate) return _lanes.TryGetValue(ownerId, out var lane) ? lane.Progress : IdleProgress;
    }

    /// <summary>Copie sous verrou. Les champs des QueueItem retournés sont mutés par les workers sous
    /// ce même verrou ; la barrière mémoire prise ici garantit donc qu'on les lit à jour, et les
    /// relire hors verrou après l'instantané est sûr. Ne pas « optimiser » en retournant _queue.</summary>
    public IReadOnlyList<QueueItem> QueueSnapshot(string ownerId)
    {
        lock (_gate) return _queue.Where(i => i.OwnerId == ownerId).ToList();
    }

    public IReadOnlyList<string> LogSnapshot(string ownerId)
    {
        lock (_gate) return _lanes.TryGetValue(ownerId, out var lane) ? lane.Log.ToList() : new List<string>();
    }

    /// <summary>Pour détecter l'arrivée de nouvelles lignes sans recopier tout le journal.</summary>
    public int LogCount(string ownerId)
    {
        lock (_gate) return _lanes.TryGetValue(ownerId, out var lane) ? lane.Log.Count : 0;
    }

    /// <summary>En attente + en cours : ce qu'affiche la pastille du menu.</summary>
    public int RemainingCount(string ownerId)
    {
        lock (_gate) return _queue.Count(i => i.OwnerId == ownerId && !i.IsFinished);
    }

    /// <summary>Déjà en file (en attente ou en cours) : le bouton correspondant est grisé.</summary>
    public bool IsQueued(string ownerId, string url)
    {
        if (!TryParseQobuzUrl(url, out var canonical, out _, out _)) return false;
        lock (_gate) return _activeUrls.Contains(Key(ownerId, canonical));
    }

    /// <summary>Terminé avec succès : le bouton reste cliquable, avec une marque ✓.</summary>
    public bool IsDone(string ownerId, string url)
    {
        if (!TryParseQobuzUrl(url, out var canonical, out _, out _)) return false;
        lock (_gate) return _doneUrls.Contains(Key(ownerId, canonical));
    }

    private static string Key(string ownerId, string url) => $"{ownerId}|{url}";

    // ─── Analyse d'URL ───

    /// <summary>
    /// Valide une URL Qobuz et en extrait la forme canonique play.qobuz.com/{kind}/{refId}.
    /// Pure et statique : appelée à l'ajout, jamais au traitement, pour que deux écritures de la
    /// même adresse (avec ou sans ?utm_source=…) produisent la même clé et donc un seul élément.
    /// </summary>
    public static bool TryParseQobuzUrl(string? input, out string canonicalUrl, out string kind, out string refId)
    {
        canonicalUrl = ""; kind = ""; refId = "";
        if (string.IsNullOrWhiteSpace(input)) return false;

        var url = input.Trim();

        // Couper requête et fragment AVANT toute chose : QobuzUrlRegex se termine par [^\s]+$ et
        // les laisserait passer jusque dans l'identifiant, créant un doublon invisible.
        var cut = url.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) url = url[..cut];

        url = url.TrimEnd('/');
        if (url.EndsWith("/releases", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/releases".Length];

        if (!QobuzUrlRegex.IsMatch(url)) return false;

        // Lien boutique (qobuz.com/fr-fr/album/nom/id) → lien lecteur
        var storeMatch = QobuzStoreLinkRegex.Match(url);
        if (storeMatch.Success)
        {
            var storeType = storeMatch.Groups["type"].Value;
            var storeId = storeMatch.Groups["id"].Value;
            if (storeType == "album") url = $"https://play.qobuz.com/album/{storeId}";
            else if (storeType == "interpreter") url = $"https://play.qobuz.com/artist/{storeId}";
        }

        var linkMatch = QobuzLinkIdGrabRegex.Match(url);
        if (!linkMatch.Success) return false;

        kind = linkMatch.Groups["type"].Value;
        refId = linkMatch.Groups["id"].Value;   // pour « user », contient des « / » (library/favorites/albums)
        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(refId)) return false;

        canonicalUrl = $"https://play.qobuz.com/{kind}/{refId}";
        return true;
    }

    // ─── Ajout à la file ───

    public EnqueueResult Enqueue(string ownerId, string url, string? label = null, string? subLabel = null, string? thumbnail = null)
    {
        if (!TryParseQobuzUrl(url, out var canonical, out var kind, out var refId))
        {
            Log(ownerId, TranslatorFor(ownerId).T("log.invalidUrl", url));
            return EnqueueResult.InvalidUrl;
        }

        EnqueueResult result;
        lock (_gate)
        {
            var existing = _queue.FirstOrDefault(i => i.OwnerId == ownerId
                && string.Equals(i.Url, canonical, StringComparison.OrdinalIgnoreCase));

            if (existing is { IsFinished: false })
            {
                result = EnqueueResult.AlreadyQueued;
            }
            else if (existing != null)
            {
                // Déjà traité : le remettre en attente en fin de file plutôt que d'en créer un
                // doublon, sinon l'unicité par URL ne tiendrait plus.
                existing.Status = QueueItemStatus.Pending;
                existing.Error = null;
                existing.StartedAt = null;
                existing.FinishedAt = null;
                existing.EnqueuedAt = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(label)) existing.Label = label!;
                if (!string.IsNullOrWhiteSpace(subLabel)) existing.SubLabel = subLabel;
                if (!string.IsNullOrWhiteSpace(thumbnail)) existing.Thumbnail = thumbnail;
                _queue.Remove(existing);
                _queue.Add(existing);
                ReindexLocked();
                result = EnqueueResult.Requeued;
            }
            else
            {
                _queue.Add(new QueueItem
                {
                    OwnerId = ownerId,
                    Url = canonical,
                    Kind = kind,
                    RefId = refId,
                    Label = string.IsNullOrWhiteSpace(label) ? canonical : label!,
                    SubLabel = subLabel,
                    Thumbnail = thumbnail
                });
                ReindexLocked();
                result = EnqueueResult.Added;
            }

            if (result != EnqueueResult.AlreadyQueued)
            {
                // Ajouter un élément ou cliquer « relancer » sont des actions explicites : elles
                // lèvent le blocage pour retenter une fois. Sans cela, une file bloquée faute
                // d'abonnement n'aurait aucune porte de sortie. Si les droits manquent toujours,
                // l'élément échoue de nouveau et les files se rebloquent aussitôt.
                _subscriptionBlocked = false;
                StartLaneLocked(ownerId);
            }
        }

        // Hors verrou : un abonné Blazor poste sur le dispatcher de son circuit, lever l'événement
        // sous _gate inviterait l'interblocage.
        if (result != EnqueueResult.AlreadyQueued) RaiseQueueChanged(ownerId);
        return result;
    }

    public (int added, int requeued, int duplicates, int invalid) EnqueueMany(string ownerId, IEnumerable<QueueRequest> requests)
    {
        int added = 0, requeued = 0, duplicates = 0, invalid = 0;
        foreach (var r in requests)
        {
            switch (Enqueue(ownerId, r.Url, r.Label, r.SubLabel, r.Thumbnail))
            {
                case EnqueueResult.Added: added++; break;
                case EnqueueResult.Requeued: requeued++; break;
                case EnqueueResult.AlreadyQueued: duplicates++; break;
                default: invalid++; break;
            }
        }
        return (added, requeued, duplicates, invalid);
    }

    // ─── Gestes ───
    //
    // Tous vérifient le propriétaire ici même, et pas seulement à l'écran : un utilisateur ne peut
    // agir que sur sa propre file.

    /// <summary>Saute l'album courant à l'intérieur de l'élément en cours (un artiste en compte
    /// souvent des dizaines). L'élément, lui, poursuit.</summary>
    public void SkipCurrent(string ownerId)
    {
        lock (_gate)
            if (_lanes.TryGetValue(ownerId, out var lane)) lane.SkipCurrent = true;
    }

    /// <summary>Abandonne l'élément en cours de cet utilisateur dans son entier ; son worker enchaîne
    /// sur le suivant. Les files des autres ne sont pas touchées.</summary>
    public void CancelCurrent(string ownerId)
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _lanes.TryGetValue(ownerId, out var lane) ? lane.ItemCts : null;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Purge les éléments en attente. Celui qui tourne va au bout — c'est la différence
    /// avec « ANNULER », et elle est volontaire.</summary>
    public void ClearPending(string ownerId)
    {
        bool changed;
        lock (_gate)
        {
            changed = _queue.RemoveAll(i => i.OwnerId == ownerId && i.Status == QueueItemStatus.Pending) > 0;
            if (changed) ReindexLocked();
        }
        if (changed) RaiseQueueChanged(ownerId);
    }

    public void ClearFinished(string ownerId)
    {
        bool changed;
        lock (_gate)
        {
            changed = _queue.RemoveAll(i => i.OwnerId == ownerId && i.IsFinished) > 0;
            if (changed) ReindexLocked();
        }
        if (changed) RaiseQueueChanged(ownerId);
    }

    public void Remove(string ownerId, string itemId)
    {
        bool changed;
        lock (_gate)
        {
            // L'élément en cours ne se retire pas : il faut d'abord l'annuler.
            changed = _queue.RemoveAll(i => i.Id == itemId && i.OwnerId == ownerId
                                            && i.Status != QueueItemStatus.Running) > 0;
            if (changed) ReindexLocked();
        }
        if (changed) RaiseQueueChanged(ownerId);
    }

    public void MoveUp(string ownerId, string itemId) => Move(ownerId, itemId, -1);
    public void MoveDown(string ownerId, string itemId) => Move(ownerId, itemId, +1);

    private void Move(string ownerId, string itemId, int direction)
    {
        bool changed = false;
        lock (_gate)
        {
            var idx = _queue.FindIndex(i => i.Id == itemId && i.OwnerId == ownerId);
            if (idx >= 0 && _queue[idx].Status == QueueItemStatus.Pending)
            {
                // On n'échange qu'avec un autre élément en attente du même utilisateur : les terminés,
                // celui qui tourne et les éléments des autres gardent leur place, sinon l'ordre
                // affiché sauterait sans raison visible.
                var target = idx + direction;
                while (target >= 0 && target < _queue.Count
                       && !(_queue[target].OwnerId == ownerId && _queue[target].Status == QueueItemStatus.Pending))
                    target += direction;

                if (target >= 0 && target < _queue.Count)
                {
                    (_queue[idx], _queue[target]) = (_queue[target], _queue[idx]);
                    changed = true;   // l'ordre change, pas les statuts : pas de réindexation
                }
            }
        }
        if (changed) RaiseQueueChanged(ownerId);
    }

    public void Retry(string ownerId, string itemId)
    {
        bool changed = false;
        lock (_gate)
        {
            var item = _queue.FirstOrDefault(i => i.Id == itemId && i.OwnerId == ownerId);
            if (item is { IsFinished: true })
            {
                item.Status = QueueItemStatus.Pending;
                item.Error = null;
                item.StartedAt = null;
                item.FinishedAt = null;
                item.EnqueuedAt = DateTime.UtcNow;
                _queue.Remove(item);
                _queue.Add(item);
                ReindexLocked();
                // « Relancer » est le geste naturel après un renouvellement d'abonnement : il lève
                // le blocage, sans quoi le clic resterait sans effet.
                _subscriptionBlocked = false;
                StartLaneLocked(ownerId);
                changed = true;
            }
        }
        if (changed) RaiseQueueChanged(ownerId);
    }

    public void ClearLog(string ownerId)
    {
        Lane? lane;
        lock (_gate)
        {
            if (_lanes.TryGetValue(ownerId, out lane)) lane.Log.Clear();
        }
        if (lane != null) RaiseProgress(lane, force: true);
    }

    /// <summary>
    /// Reprend les files interrompues faute d'abonnement. Appelé depuis les Paramètres après une
    /// actualisation de l'état du compte : sans cela, les éléments restés en attente n'auraient pour
    /// seule issue qu'un clic sur « relancer », élément par élément. Le compte étant partagé, ce sont
    /// les files de tous les utilisateurs qui repartent.
    /// </summary>
    public void ResumeAfterSubscriptionBlock()
    {
        bool wasBlocked;
        lock (_gate)
        {
            wasBlocked = _subscriptionBlocked;
            _subscriptionBlocked = false;

            if (wasBlocked)
            {
                var owners = _queue.Where(i => i.Status == QueueItemStatus.Pending)
                                   .Select(i => i.OwnerId)
                                   .Distinct()
                                   .ToList();
                foreach (var owner in owners) StartLaneLocked(owner);
            }
        }

        if (wasBlocked) RaiseQueueChanged(null);
    }

    /// <summary>
    /// À la suppression d'un compte : ses éléments en attente disparaissent, celui en cours est
    /// annulé, son journal est vidé. Les fichiers déjà téléchargés ne sont pas touchés.
    /// </summary>
    public void RemoveAllFor(string ownerId)
    {
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            _queue.RemoveAll(i => i.OwnerId == ownerId && i.Status != QueueItemStatus.Running);
            ReindexLocked();
            if (_lanes.TryGetValue(ownerId, out var lane))
            {
                cts = lane.ItemCts;
                lane.Log.Clear();
            }
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        RaiseQueueChanged(ownerId);
    }

    // ─── Mécanique interne ───

    /// <summary>À appeler sous _gate après toute mutation de statut ou de composition de la file.
    /// Reconstruire les index plutôt que d'y appliquer des deltas : la file compte quelques
    /// dizaines d'éléments au plus, et un index dérivé ne peut alors pas diverger de sa source.</summary>
    private void ReindexLocked()
    {
        _activeUrls.Clear();
        _doneUrls.Clear();
        foreach (var it in _queue)
        {
            if (!it.IsFinished) _activeUrls.Add(Key(it.OwnerId, it.Url));
            else if (it.Status == QueueItemStatus.Completed) _doneUrls.Add(Key(it.OwnerId, it.Url));
        }
    }

    private Lane LaneLocked(string ownerId)
    {
        if (!_lanes.TryGetValue(ownerId, out var lane))
        {
            lane = new Lane(ownerId);
            _lanes[ownerId] = lane;
        }
        return lane;
    }

    /// <summary>
    /// Démarrage paresseux du worker de cette file : aucun thread ne tourne tant que rien n'a été
    /// demandé. Sous _gate, comme le retrait du worker dans <see cref="LaneLoopAsync"/> : il n'existe
    /// donc aucune fenêtre où un ajout trouverait un worker sur le départ et resterait en attente
    /// sans personne pour le traiter. Task.Run se contente de mettre en file d'attente du pool, donc
    /// pas de réentrance ici.
    /// </summary>
    private void StartLaneLocked(string ownerId)
    {
        var lane = LaneLocked(ownerId);
        lane.Worker ??= Task.Run(() => LaneLoopAsync(lane));
    }

    private LocalizationService.Translator TranslatorFor(string ownerId) =>
        _loc.In(_users.Find(ownerId)?.UiLanguage);

    /// <summary>Mute le statut sous verrou puis notifie hors verrou. Toutes les transitions passent
    /// par ici, c'est ce qui garantit que les index restent cohérents.</summary>
    private void SetStatus(QueueItem item, QueueItemStatus status, string? error = null)
    {
        lock (_gate)
        {
            item.Status = status;
            item.Error = error;
            if (item.IsFinished) item.FinishedAt = DateTime.UtcNow;
            ReindexLocked();
        }
        RaiseQueueChanged(item.OwnerId);
    }

    private void RaiseProgress(Lane lane, bool force = false)
    {
        // Sans cet étranglement, la boucle d'octets de DownloadStreamToFileAsync redessinerait la
        // page à chaque bloc de 80 Ko, soit des dizaines de fois par seconde. Par file : deux
        // téléchargements simultanés ne doivent pas se voler leurs rafraîchissements.
        if (!force && Environment.TickCount64 - lane.LastProgressTick < 200) return;
        lane.LastProgressTick = Environment.TickCount64;
        RaiseSafely(OnProgressChanged, lane.OwnerId, nameof(OnProgressChanged));
    }

    private void RaiseQueueChanged(string? ownerId) => RaiseSafely(OnQueueChanged, ownerId, nameof(OnQueueChanged));

    /// <summary>
    /// Invoque chaque abonné séparément, en absorbant ses exceptions. Deux raisons, toutes deux
    /// vécues : un abonné Blazor dont le circuit vient de se fermer lève ObjectDisposedException,
    /// et une exception remontant depuis un worker le tuerait — ou, pire, ferait passer l'élément
    /// en cours pour un échec. Parcourir la liste d'invocation plutôt que d'appeler le délégué
    /// multicast évite qu'un onglet mort prive les autres de leur notification.
    /// </summary>
    private void RaiseSafely(Action<string?>? handler, string? ownerId, string name)
    {
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<string?>)d)(ownerId); }
            catch (Exception ex) { _logger.LogDebug(ex, "{Event}: un abonné a levé une exception", name); }
        }
    }

    /// <summary>
    /// Purge par tranches : un RemoveRange de 400 lignes toutes les 400 lignes coûte bien moins
    /// qu'un décalage du tableau à chaque ajout. Aucun effacement automatique en dehors de cette
    /// borne — avec une file qui enchaîne, il serait toujours arbitraire. Sous _gate.
    /// </summary>
    private static void AppendLogLocked(Lane lane, string msg)
    {
        lane.Log.Add(msg);
        if (lane.Log.Count > LogMaxLines) lane.Log.RemoveRange(0, LogTrimLines);
    }

    private void Log(string ownerId, string msg)
    {
        Lane lane;
        lock (_gate)
        {
            lane = LaneLocked(ownerId);
            AppendLogLocked(lane, msg);
        }
        RaiseProgress(lane, force: true);
    }

    private void Log(ItemRun run, string msg) => Log(run.Lane.OwnerId, msg);

    private void UpdateProgress(ItemRun run, int percent, string status, string currentItem = "", int step = 0, int steps = 0)
    {
        run.Progress.Percent = percent;
        run.Progress.Status = status;
        // Remis à zéro par tout appel qui ne parle pas de pistes : des segments restés d'un album
        // précédent ne doivent pas s'afficher sous une discographie.
        run.Progress.Step = step;
        run.Progress.Steps = steps;
        if (!string.IsNullOrEmpty(currentItem)) run.Progress.CurrentItem = currentItem;
        RaiseProgress(run.Lane, force: true);
    }

    // ─── Worker ───

    /// <summary>
    /// Worker d'une file : traite les éléments en attente de son propriétaire, un par un, puis se
    /// retire. Un worker par utilisateur, qui tournent en parallèle.
    /// </summary>
    private async Task LaneLoopAsync(Lane lane)
    {
        var processedAny = false;

        while (true)
        {
            var l = TranslatorFor(lane.OwnerId);
            QueueItem? item;
            var drained = false;

            lock (_gate)
            {
                // Abonnement manquant : on s'arrête ici et les éléments suivants restent en
                // attente, prêts à repartir si l'abonnement est renouvelé.
                item = _appCt.IsCancellationRequested || _subscriptionBlocked
                    ? null
                    : _queue.FirstOrDefault(i => i.OwnerId == lane.OwnerId && i.Status == QueueItemStatus.Pending);

                if (item != null)
                {
                    item.Status = QueueItemStatus.Running;
                    item.StartedAt = DateTime.UtcNow;
                    ReindexLocked();
                }
                else
                {
                    // Retrait sous le verrou même qui sert au démarrage (StartLaneLocked) : un ajout
                    // concurrent voit soit ce worker encore inscrit — et c'est lui qui le traitera au
                    // tour suivant —, soit plus aucun, et il en démarre un.
                    lane.Worker = null;

                    // File vidée : remettre la carte de progression au repos, sans quoi l'album et
                    // la vitesse du dernier téléchargement resteraient affichés indéfiniment. Ici,
                    // sous le verrou, et non après : un worker redémarré entre-temps écraserait sa
                    // propre progression avec cette remise à zéro.
                    if (processedAny && !_appCt.IsCancellationRequested)
                    {
                        var p = lane.Progress;
                        p.IsRunning = false;
                        p.IsComplete = true;
                        p.CurrentQueueItemId = null;
                        p.CurrentAlbum = null;
                        p.CurrentItem = "";
                        p.Speed = "";
                        p.Percent = 0;
                        p.Status = "";
                        AppendLogLocked(lane, _subscriptionBlocked
                            ? l["log.queueBlockedNoSubscription"]
                            : l["log.queueDrained"]);
                        drained = true;
                    }
                }
            }

            if (item == null)
            {
                // Hors verrou, comme toujours pour les événements.
                if (drained)
                {
                    RaiseProgress(lane, force: true);
                    RaiseQueueChanged(lane.OwnerId);
                }
                return;
            }

            processedAny = true;
            RaiseQueueChanged(lane.OwnerId);

            try
            {
                await RunItemAsync(lane, item);
            }
            catch (Exception ex)
            {
                // Filet de sécurité : le worker ne meurt jamais sur l'échec d'un élément,
                // sans quoi toute la file resterait bloquée en attente pour toujours.
                _logger.LogError(ex, "Queue worker: unhandled failure on {Url}", item.Url);
                try { SetStatus(item, QueueItemStatus.Failed, ex.Message); } catch { }
            }
        }
    }

    private async Task RunItemAsync(Lane lane, QueueItem item)
    {
        var owner = _users.Find(item.OwnerId);
        if (owner == null)
        {
            // Compte supprimé entre l'ajout et le traitement : personne ne verra plus cet élément.
            SetStatus(item, QueueItemStatus.Cancelled);
            return;
        }

        var l = _loc.In(owner.UiLanguage);

        // Culture du propriétaire pour tout ce qui suit — vitesse, dates des messages : le worker
        // hérite sinon de celle de la requête qui l'a démarré, qui peut être celle d'un autre
        // utilisateur. Le changement ne vaut que pour cette méthode et ce qu'elle attend.
        CultureInfo.CurrentCulture = l.Culture;
        CultureInfo.CurrentUICulture = l.Culture;

        // Lié au jeton applicatif : l'arrêt du conteneur interrompt le téléchargement au lieu
        // d'attendre le délai de grâce de docker stop.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_appCt);
        lane.ItemCts = cts;
        lane.SkipCurrent = false;

        var run = new ItemRun(lane, owner.Settings, l, cts.Token);

        var p = lane.Progress;
        p.IsRunning = true;
        p.IsComplete = false;
        p.Percent = 0;
        p.Speed = "";
        p.CurrentAlbum = null;
        p.CurrentItem = item.Label;
        p.CurrentQueueItemId = item.Id;
        RaiseProgress(lane, force: true);

        // Séparateur par élément : remplace l'effacement automatique du journal, qui n'a plus de
        // sens dès lors que plusieurs téléchargements s'enchaînent.
        Log(run, l.T("log.queueItemStart", item.Label));

        try
        {
            Log(run, l["log.checkingLink"]);

            switch (item.Kind)
            {
                case "album":
                    await DownloadAlbumAsync(run, item.RefId);
                    break;
                case "track":
                    await DownloadSingleTrackAsync(run, item.RefId);
                    break;
                case "playlist":
                    await DownloadPlaylistAsync(run, item.RefId);
                    break;
                case "artist":
                    await DownloadArtistAsync(run, item.RefId);
                    break;
                case "label":
                    await DownloadLabelAsync(run, item.RefId);
                    break;
                case "user":
                    if (item.RefId.Contains("albums"))
                        await DownloadFavoritesAlbumsAsync(run);
                    else if (item.RefId.Contains("tracks"))
                        await DownloadFavoritesTracksAsync(run);
                    else if (item.RefId.Contains("artists"))
                        await DownloadFavoritesArtistsAsync(run);
                    else
                        throw new NotSupportedException(l["log.unsupportedUserLink"]);
                    break;
                default:
                    throw new NotSupportedException(l.T("log.unsupportedLinkType", item.Kind));
            }

            Log(run, l["log.complete"]);
            SetStatus(item, QueueItemStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            // Annulation utilisateur : on le signale et on enchaîne. Arrêt applicatif : inutile de
            // journaliser, le processus s'éteint et la boucle sortira sur _appCt.
            if (!_appCt.IsCancellationRequested) Log(run, l["log.aborted"]);
            SetStatus(item, QueueItemStatus.Cancelled);
        }
        catch (SubscriptionRequiredException ex)
        {
            // Rien à réessayer : ce n'est pas un incident réseau mais un droit manquant. On bloque
            // toutes les files ici, avant que les éléments suivants ne répètent le même échec.
            lock (_gate) _subscriptionBlocked = true;
            Log(run, l.T("log.error", ex.Message));
            _logger.LogWarning("Download blocked, account has no streaming rights: {Url}", item.Url);
            SetStatus(item, QueueItemStatus.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            Log(run, l.T("log.error", ex.Message));
            _logger.LogError(ex, "Download failed for {Url}", item.Url);
            SetStatus(item, QueueItemStatus.Failed, ex.Message);
        }
        finally
        {
            lane.ItemCts = null;
            cts.Dispose();
            p.CurrentQueueItemId = null;
            p.CurrentAlbum = null;
            p.Speed = "";
            RaiseProgress(lane, force: true);
        }
    }

    // ─── Album ───

    private async Task DownloadAlbumAsync(ItemRun run, string albumId)
    {
        var album = await _api.GetAlbumFullAsync(albumId);
        run.Progress.CurrentAlbum = album;

        var s = run.Settings;
        if (!album.Streamable && s.StreamableCheck)
        {
            Log(run, run.L["log.notStreamable"]);
            return;
        }

        var downloadPath = _templates.CreatePath(s.DownloadLocation, s.ArtistTemplate, s.AlbumTemplate, null, album, null, null, s);
        Directory.CreateDirectory(downloadPath);

        // Artwork
        await DownloadArtworkAsync(run, downloadPath, album);

        var tracks = album.Tracks?.Items ?? new List<QobuzTrack>();
        int total = tracks.Count;

        for (int i = 0; i < total; i++)
        {
            run.Ct.ThrowIfCancellationRequested();
            if (run.Lane.SkipCurrent) { run.Lane.SkipCurrent = false; Log(run, run.L["log.albumSkipped"]); break; }

            var track = tracks[i];
            UpdateProgress(run, (int)((double)i / total * 100), run.L.T("download.status.track", i + 1, total), _templates.FormatTrackTitle(track), step: i, steps: total);

            try
            {
                // Get full track info
                var trackInfo = await _api.GetTrackAsync(track.Id.ToString());
                if (trackInfo == null) { Log(run, run.L.T("log.trackUnavailable", track.TrackNumber)); continue; }
                if (!trackInfo.Streamable && s.StreamableCheck) { Log(run, run.L.T("log.trackNotStreamable", track.TrackNumber)); continue; }

                await DownloadTrackToFileAsync(run, "album", album, trackInfo, downloadPath, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (SubscriptionRequiredException) { throw; }
            catch (Exception ex) { Log(run, run.L.T("log.trackFailed", track.Id, ex.Message)); }
        }

        // Goodies
        if (s.DownloadGoodies && album.Goodies != null)
        {
            foreach (var goody in album.Goodies)
            {
                if (goody.FileFormatId == 52 || string.IsNullOrEmpty(goody.Url)) continue;
                await DownloadGoodyAsync(run, downloadPath, album, goody);
            }
        }

        DeleteTempArtwork(run);
    }

    // ─── Single Track ───

    private async Task DownloadSingleTrackAsync(ItemRun run, string trackId)
    {
        var track = await _api.GetTrackAsync(trackId);
        if (track == null) { Log(run, run.L["log.trackNotFound"]); return; }

        QobuzAlbum? album = null;
        if (track.Album != null)
            album = await _api.GetAlbumFullAsync(track.Album.Id);

        if (album == null) { Log(run, run.L["log.albumInfoUnavailable"]); return; }
        run.Progress.CurrentAlbum = album;

        var s = run.Settings;
        var downloadPath = _templates.CreatePath(s.DownloadLocation, s.ArtistTemplate, s.AlbumTemplate, null, album, track, null, s);
        Directory.CreateDirectory(downloadPath);

        await DownloadArtworkAsync(run, downloadPath, album);
        await DownloadTrackToFileAsync(run, "track", album, track, downloadPath, null);
        DeleteTempArtwork(run);
    }

    // ─── Playlist ───

    private async Task DownloadPlaylistAsync(ItemRun run, string playlistId)
    {
        var playlist = await _api.GetPlaylistFullAsync(playlistId);
        var tracks = playlist.Tracks?.Items ?? new List<QobuzTrack>();
        int total = tracks.Count;
        var s = run.Settings;

        for (int i = 0; i < total; i++)
        {
            run.Ct.ThrowIfCancellationRequested();
            var item = tracks[i];
            UpdateProgress(run, (int)((double)i / total * 100), run.L.T("download.status.track", i + 1, total), step: i, steps: total);

            try
            {
                if (s.UseItemPosInPlaylist) item.TrackNumber = item.Position;

                var trackInfo = await _api.GetTrackAsync(item.Id.ToString());
                if (trackInfo == null) continue;

                QobuzAlbum? album = trackInfo.Album != null ? await _api.GetAlbumFullAsync(trackInfo.Album.Id) : null;
                if (album == null) continue;
                run.Progress.CurrentAlbum = album;

                var downloadPath = _templates.CreatePath(s.DownloadLocation, s.ArtistTemplate, s.AlbumTemplate, s.PlaylistTemplate, album, item, playlist, s);
                Directory.CreateDirectory(downloadPath);

                await DownloadArtworkAsync(run, downloadPath, album);
                await DownloadTrackToFileAsync(run, "playlist", album, item, downloadPath, playlist);
                DeleteTempArtwork(run);
            }
            catch (OperationCanceledException) { throw; }
            catch (SubscriptionRequiredException) { throw; }
            catch (Exception ex) { Log(run, run.L.T("log.playlistTrackFailed", item.Id, ex.Message)); }
        }
    }

    // ─── Artist ───

    private async Task DownloadArtistAsync(ItemRun run, string artistId)
    {
        var artist = await _api.GetArtistFullAsync(artistId);
        var albums = artist.Albums?.Items ?? new List<QobuzAlbum>();
        int total = albums.Count;

        for (int i = 0; i < total; i++)
        {
            run.Ct.ThrowIfCancellationRequested();
            if (run.Lane.SkipCurrent) { run.Lane.SkipCurrent = false; Log(run, run.L["log.albumSkipped"]); continue; }

            UpdateProgress(run, (int)((double)i / total * 100), run.L.T("download.status.album", i + 1, total), albums[i].Title);
            try { await DownloadAlbumAsync(run, albums[i].Id); }
            catch (OperationCanceledException) { throw; }
            catch (SubscriptionRequiredException) { throw; }
            catch (Exception ex) { Log(run, run.L.T("log.albumFailed", albums[i].Id, ex.Message)); }
        }
    }

    // ─── Label ───

    private async Task DownloadLabelAsync(ItemRun run, string labelId)
    {
        var label = await _api.GetLabelAsync(labelId);
        if (label?.Albums?.Items == null) { Log(run, run.L["log.labelNotFound"]); return; }

        var albums = label.Albums.Items;
        int total = albums.Count;

        for (int i = 0; i < total; i++)
        {
            run.Ct.ThrowIfCancellationRequested();
            if (run.Lane.SkipCurrent) { run.Lane.SkipCurrent = false; continue; }

            UpdateProgress(run, (int)((double)i / total * 100), run.L.T("download.status.album", i + 1, total), albums[i].Title);
            try { await DownloadAlbumAsync(run, albums[i].Id); }
            catch (OperationCanceledException) { throw; }
            catch (SubscriptionRequiredException) { throw; }
            catch (Exception ex) { Log(run, run.L.T("log.albumFailed", albums[i].Id, ex.Message)); }
        }
    }

    // ─── Favorites ───
    //
    // Les favoris sont ceux du compte Qobuz, donc de l'instance : l'identifiant vient des réglages
    // partagés et non de ceux de l'utilisateur.

    private async Task DownloadFavoritesAlbumsAsync(ItemRun run)
    {
        var favs = await _api.GetFavoritesAsync(_settings.Settings.UserId, "albums");
        if (favs?.Albums?.Items == null) return;

        var albums = favs.Albums.Items;
        int total = albums.Count;
        for (int i = 0; i < total; i++)
        {
            run.Ct.ThrowIfCancellationRequested();
            UpdateProgress(run, (int)((double)i / total * 100), run.L.T("download.status.favAlbum", i + 1, total));
            try { await DownloadAlbumAsync(run, albums[i].Id); }
            catch (OperationCanceledException) { throw; }
            catch (SubscriptionRequiredException) { throw; }
            catch (Exception ex) { Log(run, run.L.T("log.favAlbumFailed", albums[i].Id, ex.Message)); }
        }
    }

    private async Task DownloadFavoritesTracksAsync(ItemRun run)
    {
        var favs = await _api.GetFavoritesAsync(_settings.Settings.UserId, "tracks");
        if (favs?.Tracks?.Items == null) return;

        var tracks = favs.Tracks.Items;
        int total = tracks.Count;
        for (int i = 0; i < total; i++)
        {
            run.Ct.ThrowIfCancellationRequested();
            UpdateProgress(run, (int)((double)i / total * 100), run.L.T("download.status.favTrack", i + 1, total));
            try { await DownloadSingleTrackAsync(run, tracks[i].Id.ToString()); }
            catch (OperationCanceledException) { throw; }
            catch (SubscriptionRequiredException) { throw; }
            catch (Exception ex) { Log(run, run.L.T("log.favTrackFailed", tracks[i].Id, ex.Message)); }
        }
    }

    private async Task DownloadFavoritesArtistsAsync(ItemRun run)
    {
        var favs = await _api.GetFavoritesAsync(_settings.Settings.UserId, "artists");
        if (favs?.Artists?.Items == null) return;

        foreach (var artist in favs.Artists.Items)
        {
            run.Ct.ThrowIfCancellationRequested();
            try { await DownloadArtistAsync(run, artist.Id.ToString()); }
            catch (OperationCanceledException) { throw; }
            catch (SubscriptionRequiredException) { throw; }
            catch (Exception ex) { Log(run, run.L.T("log.favArtistFailed", artist.Id, ex.Message)); }
        }
    }

    // ─── Core: download a single track to file ───

    private async Task DownloadTrackToFileAsync(ItemRun run, string downloadType, QobuzAlbum album, QobuzTrack track, string downloadPath, QobuzPlaylist? playlist)
    {
        var s = run.Settings;
        var l = run.L;
        var ct = run.Ct;

        // Build file path
        var trackTemplate = _templates.ApplyTemplate(s.TrackTemplate, album, track, playlist, s);
        var filePath = Path.Combine(downloadPath, trackTemplate.TrimEnd() + s.AudioFormat);

        // Handle multi-disc
        if (track.PlaylistTrackId == null && downloadType != "track" && album.MediaCount > 1 && !string.IsNullOrEmpty(s.CdTemplate))
        {
            var cdDir = Regex.Replace(s.CdTemplate, "%DiscNumber%", track.MediaNumber.ToString(), RegexOptions.IgnoreCase);
            filePath = Path.Combine(downloadPath, cdDir, trackTemplate.TrimEnd() + s.AudioFormat);
        }

        // Skip existing
        if (s.DuplicateFileMode == "skip" && System.IO.File.Exists(filePath))
        {
            Log(run, l.T("log.fileExists", track.TrackNumber));
            return;
        }

        // Prepare temp path (reused across retry attempts)
        var configPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "/config";
        var tempDir = Path.Combine(configPath, "qbdlx-temp");
        Directory.CreateDirectory(tempDir);

        // L'extension doit suivre la qualité réellement servie, et non celle demandée : la chaîne
        // de repli ci-dessous peut descendre jusqu'au MP3 320 alors que les Paramètres annoncent du
        // FLAC. Elle est donc recalculée à chaque palier, et le chemin final corrigé après coup.
        // Le nom porte l'identifiant de session de l'élément : deux utilisateurs peuvent télécharger
        // la même piste au même moment, et un nom fondé sur la seule piste ferait écrire l'un dans
        // le fichier de l'autre.
        var fileExtension = s.AudioFormat;
        var tempFile = Path.Combine(tempDir, $"qbdlx_downloading-{run.SessionId}-{track.Id}{fileExtension}");

        var titleFormatted = _templates.FormatTrackTitle(track);
        Log(run, l.T("log.downloadingTrack", track.TrackNumber.ToString().PadLeft(2, '0'), titleFormatted));

        // Try requested quality, then fall back to lower qualities if download keeps failing.
        // For each quality we make a few silent attempts (with a fresh signed URL each time)
        // before giving up and stepping down — most transient CDN cuts are fixed by a quick retry.
        // Qobuz CDN sometimes throttles / cuts very-high-res files (24bit/192kHz) for certain accounts.
        const int attemptsPerQuality = 3;
        var qualityChain = BuildQualityFallbackChain(s.QualityFormat);
        Exception? lastError = null;
        bool downloaded = false;

        // Comptage des qualités qui n'ont renvoyé qu'un extrait de 30 s. Un compte sans Hi-Res en
        // renvoie un pour le 24 bits puis sert le FLAC 16 bits sans broncher : seul un extrait sur
        // *toute* la chaîne, MP3 320 compris, signe une absence de droits.
        int triedQualities = 0;
        int sampleOnlyQualities = 0;

        foreach (var (qFormat, qLabel) in qualityChain)
        {
            if (qFormat != s.QualityFormat)
            {
                Log(run, l.T("log.qualityUnavailable", qLabel));
            }

            // Format 5 = MP3 320 : le fichier n'est pas un FLAC, quoi qu'en disent les Paramètres.
            // Nommer correctement le fichier temporaire suffit à ce que l'étiqueteur écrive de
            // l'ID3 plutôt que des commentaires Vorbis — il choisit son analyseur sur l'extension.
            fileExtension = qFormat == "5" ? ".mp3" : s.AudioFormat;
            tempFile = Path.Combine(tempDir, $"qbdlx_downloading-{run.SessionId}-{track.Id}{fileExtension}");

            Exception? qualityError = null;
            bool qualityUnavailable = false;
            bool sampleOnly = false;

            for (int attempt = 1; attempt <= attemptsPerQuality; attempt++)
            {
                try
                {
                    var stream = await _api.GetStreamUrlAsync(track.Id.ToString(), qFormat);
                    if (stream == null || string.IsNullOrEmpty(stream.Url))
                    {
                        qualityUnavailable = true;
                        break;
                    }

                    // Deux signaux, parce qu'aucun n'est suffisant à lui seul :
                    //  - « sample » : Qobuz annonce explicitement un extrait de 30 s ;
                    //  - type MIME incohérent avec la qualité demandée : on a réclamé du FLAC et
                    //    reçu du MP3. C'est ce cas-là qui produisait les « FLAC stream not found at
                    //    starting position » en rafale — l'extrait était écrit dans un fichier
                    //    .flac, puis confié à l'étiqueteur qui n'y trouvait aucun flux FLAC.
                    //
                    // Rien à réessayer ici : les deux tentatives suivantes renverraient le même
                    // extrait. On passe directement à la qualité inférieure, ce qui ramène 12
                    // appels par piste à 4 dans le pire des cas.
                    var wantsFlac = qFormat != "5";
                    // MIME absent : ne rien conclure, plutôt que de refuser un fichier valide.
                    var gotFlac = stream.MimeType?.Contains("flac", StringComparison.OrdinalIgnoreCase) ?? true;

                    if (stream.Sample || (wantsFlac && !gotFlac))
                    {
                        var codes = stream.Restrictions is { Count: > 0 }
                            ? stream.Restrictions.ToString(Formatting.None)
                            : "—";
                        _logger.LogWarning(
                            "Track {Id} in {Quality}: preview only (sample={Sample}, mime={Mime}, restrictions={Codes})",
                            track.Id, qLabel, stream.Sample, stream.MimeType ?? "—", codes);
                        sampleOnly = true;
                        qualityUnavailable = true;
                        break;
                    }

                    await DownloadStreamToFileAsync(run, stream.Url, tempFile);
                    downloaded = true;
                    lastError = null;
                    qualityError = null;
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (SubscriptionRequiredException) { throw; }
                catch (Exception ex)
                {
                    qualityError = ex;
                    try { if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile); } catch { }

                    if (attempt < attemptsPerQuality)
                    {
                        // Silent retry — short backoff, no log noise
                        await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                    }
                }
            }

            if (downloaded) break;

            triedQualities++;
            if (sampleOnly) sampleOnlyQualities++;

            if (sampleOnly)
            {
                Log(run, l.T("log.trackSampleOnly", track.TrackNumber, qLabel));
            }
            else if (qualityUnavailable)
            {
                Log(run, l.T("log.trackNotInQuality", track.TrackNumber, qLabel));
            }
            else if (qualityError != null)
            {
                lastError = qualityError;
                Log(run, l.T("log.qualityFailed", qLabel, attemptsPerQuality, qualityError.Message));
            }
        }

        if (!downloaded)
        {
            // Un extrait à chaque qualité, MP3 320 inclus : ce n'est pas la piste qui manque, c'est
            // le compte qui n'a aucun droit de streaming. Les éléments suivants de la file
            // échoueraient exactement de la même façon — autant le dire une fois et s'arrêter.
            if (sampleOnlyQualities > 0 && sampleOnlyQualities == triedQualities)
                throw new SubscriptionRequiredException(l["log.subscriptionRequired"]);

            throw lastError ?? new IOException("Téléchargement impossible (toutes qualités échouées)");
        }

        // La qualité servie n'est pas celle demandée : aligner le nom du fichier sur son contenu,
        // plutôt que de livrer un MP3 sous une extension .flac — que la plupart des lecteurs et des
        // étiqueteurs refusent. Seule l'extension est corrigée : les variables %TrackFormat% du
        // modèle de nom, elles, sont résolues avant le téléchargement et annoncent donc encore la
        // qualité demandée. Les aligner supposerait de renommer aussi le dossier de l'album, créé
        // avant la première piste.
        if (!filePath.EndsWith(fileExtension, StringComparison.OrdinalIgnoreCase))
        {
            filePath = Path.ChangeExtension(filePath, fileExtension);

            // Le test d'existence du début portait sur l'extension demandée. Le refaire ici : sinon
            // un second passage sur un album où cette piste retombe toujours en MP3 ferait échouer
            // le déplacement final sur un fichier déjà présent.
            if (s.DuplicateFileMode == "skip" && System.IO.File.Exists(filePath))
            {
                try { System.IO.File.Delete(tempFile); } catch { }
                Log(run, l.T("log.fileExists", track.TrackNumber));
                return;
            }
        }

        // Fix MD5 for FLAC
        if (s.FixMD5s && fileExtension.Contains("flac", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo("flac", $"-f8 \"{tempFile}\"")
                {
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                });
                if (proc != null) await proc.WaitForExitAsync(ct);
            }
            catch (Exception ex) { _logger.LogWarning("flac MD5 fix failed: {Error}", ex.Message); }
        }

        // Tag file
        var embeddedArtPath = EmbeddedArtPath(run);
        _tagging.WriteToFile(tempFile, System.IO.File.Exists(embeddedArtPath) ? embeddedArtPath : null, album, track, s);

        // Move to final location
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (s.DuplicateFileMode == "rename" && System.IO.File.Exists(filePath))
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            var ext = Path.GetExtension(filePath);
            var dirPath = Path.GetDirectoryName(filePath) ?? downloadPath;
            int counter = 1;
            while (System.IO.File.Exists(filePath))
            {
                filePath = Path.Combine(dirPath, $"{name} ({counter}){ext}");
                counter++;
            }
        }

        System.IO.File.Move(tempFile, filePath, s.DuplicateFileMode == "overwrite");
        Log(run, l["log.done"]);
    }

    // ─── HTTP stream download helpers ───

    /// <summary>
    /// Returns the chain of (format_id, label) to try for downloading a track.
    /// Starts with the user's chosen quality, then falls back to lower qualities
    /// if Qobuz refuses or throttles the high-res stream.
    /// </summary>
    private static IEnumerable<(string format, string label)> BuildQualityFallbackChain(string requested)
    {
        // Quality order from highest to lowest:
        //  27 = FLAC 24bit 192kHz, 7 = FLAC 24bit 96kHz, 6 = FLAC 16bit 44.1kHz, 5 = MP3 320
        var allQualities = new (string format, string label)[]
        {
            ("27", "FLAC 24bit/192kHz"),
            ("7",  "FLAC 24bit/96kHz"),
            ("6",  "FLAC 16bit/44.1kHz"),
            ("5",  "MP3 320"),
        };

        // Find requested quality, then yield it followed by lower ones
        bool foundRequested = false;
        foreach (var q in allQualities)
        {
            if (q.format == requested) foundRequested = true;
            if (foundRequested) yield return q;
        }
        // If the requested quality wasn't in our list (e.g. unknown id), just try it raw
        if (!foundRequested)
        {
            yield return (requested, $"format {requested}");
            foreach (var q in allQualities) yield return q;
        }
    }

    /// <summary>
    /// Downloads a signed Qobuz stream URL to a local temp file in one shot.
    /// Throws on any failure (caller handles fallback to a lower quality).
    /// </summary>
    private async Task DownloadStreamToFileAsync(ItemRun run, string url, string tempFile)
    {
        var ct = run.Ct;

        // Start clean — never resume; signed URLs are essentially single-use here.
        if (System.IO.File.Exists(tempFile))
        {
            try { System.IO.File.Delete(tempFile); } catch { }
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1;
        long bytesRead = 0;
        var buffer = new byte[81920];
        var sw = Stopwatch.StartNew();

        await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true))
        await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
        {
            int read;
            while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                bytesRead += read;

                // sw n'est jamais redémarré : il sert à calculer la vitesse *moyenne* depuis le
                // début du fichier. Le cadencement de l'affichage, lui, est assuré par
                // RaiseProgress(force: false) — d'où l'horodatage séparé, propre à chaque file.
                if (totalBytes > 0 && sw.ElapsedMilliseconds > 250)
                {
                    var pct = (int)(bytesRead * 100 / totalBytes);
                    var speed = bytesRead / (sw.Elapsed.TotalSeconds + 0.001);
                    run.Progress.Speed = speed >= 1024 * 1024
                        ? $"{speed / (1024 * 1024):F2} MB/s"
                        : $"{speed / 1024:F2} KB/s";
                    run.Progress.Status = run.L.T("download.status.downloading", pct, run.Progress.Speed);
                    RaiseProgress(run.Lane, force: false);
                }
            }
        }

        if (totalBytes > 0 && bytesRead < totalBytes)
        {
            throw new IOException($"Téléchargement incomplet : {bytesRead}/{totalBytes} octets");
        }
    }

    // ─── Artwork ───

    private static string EmbeddedArtPath(ItemRun run) =>
        Path.Combine(Path.GetTempPath(), $"qbdlx-{run.SessionId}-{run.Settings.EmbeddedArtSize}.jpg");

    private async Task DownloadArtworkAsync(ItemRun run, string downloadPath, QobuzAlbum album)
    {
        var s = run.Settings;
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

        try
        {
            // Cover.jpg to disk
            if (!s.DontSaveArtworkToDisk)
            {
                var coverPath = Path.Combine(downloadPath, "Cover.jpg");
                if (!System.IO.File.Exists(coverPath))
                {
                    var url = album.Image.Large.Replace("_600", $"_{s.SavedArtSize}");
                    var data = await httpClient.GetByteArrayAsync(url);
                    Directory.CreateDirectory(downloadPath);
                    await System.IO.File.WriteAllBytesAsync(coverPath, data);
                }
            }

            // Embedded artwork to temp
            var embeddedPath = EmbeddedArtPath(run);
            if (!System.IO.File.Exists(embeddedPath))
            {
                var url = album.Image.Large.Replace("_600", $"_{s.EmbeddedArtSize}");
                var data = await httpClient.GetByteArrayAsync(url);
                await System.IO.File.WriteAllBytesAsync(embeddedPath, data);
            }
        }
        catch (Exception ex) { _logger.LogWarning("Artwork download failed: {Error}", ex.Message); }
    }

    private async Task DownloadGoodyAsync(ItemRun run, string downloadPath, QobuzAlbum album, QobuzGoody goody)
    {
        var fileName = TemplateService.MakeSafeFilename(goody.Description ?? album.Title);
        var ext = Path.GetExtension(goody.Url ?? ".pdf");
        var filePath = Path.Combine(downloadPath, $"{fileName} ({goody.Id}){ext}");

        if (System.IO.File.Exists(filePath)) { Log(run, run.L["log.goodyExists"]); return; }

        try
        {
            Log(run, run.L["log.goodyFound"]);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var data = await http.GetByteArrayAsync(goody.Url, run.Ct);
            await System.IO.File.WriteAllBytesAsync(filePath, data, run.Ct);
            Log(run, run.L["log.done"]);
        }
        catch (Exception ex) { Log(run, run.L.T("log.goodyFailed", ex.Message)); }
    }

    private static void DeleteTempArtwork(ItemRun run)
    {
        try
        {
            var path = EmbeddedArtPath(run);
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch { }
    }
}
