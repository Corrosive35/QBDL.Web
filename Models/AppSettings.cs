using System.Globalization;

namespace QBDL.Web.Models;

/// <summary>
/// Réglages de l'instance : le compte Qobuz partagé par tous les utilisateurs. Les réglages de
/// téléchargement sont propres à chaque compte et vivent dans <see cref="UserSettings"/>.
/// </summary>
public class AppSettings
{
    // Héritage de la 1.6, où l'instance n'avait qu'un mot de passe et une langue. Lus une seule fois
    // par la migration vers les comptes (UserStore), puis vidés.
    public string AppAccessPasswordHash { get; set; } = "";
    public string UiLanguage { get; set; } = "";

    // Auth
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string UserAuthToken { get; set; } = "";
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserDisplayName { get; set; } = "";
    public string UserLabel { get; set; } = "";
    public string UserAvatar { get; set; } = "";
    public bool UseTokenLogin { get; set; }

    // État de l'abonnement, relu à chaque connexion. Persisté pour que les Paramètres et la barre
    // latérale puissent l'afficher sans rappeler l'API à chaque rendu.
    public bool HasSubscription { get; set; } = true;
    public string SubscriptionOffer { get; set; } = "";
    public string SubscriptionEndDate { get; set; } = "";
    public bool SubscriptionCanceled { get; set; }
    public string UserCountry { get; set; } = "";
    public bool CanStreamLossless { get; set; } = true;
    public bool CanStreamHires { get; set; } = true;

    public string Theme { get; set; } = "dark";

    /// <summary>
    /// Reporte la fiche de compte lue chez Qobuz dans les réglages persistés. Appelé à la connexion
    /// et à chaque revalidation du jeton, pour que l'interface n'ait pas à rappeler l'API à chaque
    /// rendu. Une seule implémentation afin que les deux chemins ne divergent jamais.
    /// </summary>
    public void ApplyAccountStatus(QobuzUserInfo? user)
    {
        var p = user?.Credential?.Parameters;

        UserId = user?.Id.ToString() ?? "";
        UserDisplayName = user?.DisplayName ?? "";
        UserCountry = user?.CountryCode ?? "";

        // parameters à null = compte sans abonnement. Le jeton est pourtant valide et l'API répond
        // 200 : c'est le seul marqueur fiable.
        HasSubscription = p != null;
        UserLabel = p?.ShortLabel ?? "";
        SubscriptionOffer = p?.ShortLabel ?? user?.Subscription?.Offer ?? user?.Credential?.Label ?? "";
        SubscriptionEndDate = p?.ExpirationDate ?? user?.Subscription?.EndDate ?? "";
        SubscriptionCanceled = user?.Subscription?.IsCanceled ?? false;
        CanStreamLossless = p?.LosslessStreaming ?? false;
        CanStreamHires = p?.HiresStreaming ?? false;
    }

    /// <summary>
    /// Abonnement résilié dont la date de fin est passée.
    ///
    /// La date seule ne suffit pas : sur un abonnement reconduit automatiquement, end_date marque
    /// la fin de la période en cours et peut se retrouver dans le passé le temps que Qobuz la
    /// rafraîchisse — afficher « Expiré » dans ce cas serait une fausse alerte. On exige donc que
    /// Qobuz ait lui-même marqué l'abonnement comme non reconduit. Une date absente ou illisible
    /// est traitée comme non expirée, pour la même raison.
    ///
    /// Le cas franchement expiré, lui, est déjà couvert par <see cref="HasSubscription"/> : Qobuz
    /// vide alors credential.parameters.
    ///
    /// Une méthode et non une propriété : Newtonsoft sérialiserait cette dernière dans settings.json.
    /// </summary>
    public bool IsSubscriptionExpired()
    {
        if (!HasSubscription || !SubscriptionCanceled) return false;
        if (string.IsNullOrWhiteSpace(SubscriptionEndDate)) return false;

        return DateTime.TryParse(SubscriptionEndDate, CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out var end)
               && end.Date < DateTime.UtcNow.Date;
    }

    /// <summary>Remet à blanc tout ce qui décrit le compte, à la déconnexion.</summary>
    public void ClearAccount()
    {
        UserAuthToken = "";
        UserId = "";
        UserDisplayName = "";
        UserLabel = "";
        UserAvatar = "";
        UserCountry = "";
        SubscriptionOffer = "";
        SubscriptionEndDate = "";
        SubscriptionCanceled = false;
        HasSubscription = true;
        CanStreamLossless = true;
        CanStreamHires = true;
    }
}
