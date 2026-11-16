namespace QBDL.Web.Models;

/// <summary>
/// Réglages de téléchargement propres à un utilisateur.
/// </summary>
/// <remarks>
/// Ces champs vivaient dans <see cref="AppSettings"/> jusqu'à la 1.6. Noms et valeurs par défaut
/// sont restés identiques à dessein : désérialiser l'ancien settings.json dans cette classe suffit
/// à les récupérer, sans table de correspondance à maintenir.
/// </remarks>
public class UserSettings
{
    // Download — fixé par l'administrateur, en lecture seule pour l'utilisateur.
    public string DownloadLocation { get; set; } = "/downloads";
    public string QualityFormat { get; set; } = "27"; // 5=MP3, 6=FLAC16, 7=FLAC24<96, 27=FLAC24
    public string AudioFormat { get; set; } = ".flac";

    // Templates
    public string ArtistTemplate { get; set; } = "%ArtistName%";
    public string AlbumTemplate { get; set; } = "%AlbumTitle%";
    public string TrackTemplate { get; set; } = "%TrackNumber% - %TrackTitle%";
    public string VaTrackTemplate { get; set; } = "%TrackNumber% - %TrackArtist% - %TrackTitle%";
    public string PlaylistTemplate { get; set; } = "%PlaylistTitle%";
    public string FavoritesTemplate { get; set; } = "Favorites";
    public string CdTemplate { get; set; } = "CD %DiscNumber%";

    // Tagging
    public bool AlbumTag { get; set; } = true;
    public bool AlbumArtistTag { get; set; } = true;
    public bool ArtistTag { get; set; } = true;
    public bool TrackTitleTag { get; set; } = true;
    public bool TrackTag { get; set; } = true;
    public bool TotalTracksTag { get; set; } = true;
    public bool DiscTag { get; set; } = true;
    public bool TotalDiscsTag { get; set; } = true;
    public bool GenreTag { get; set; } = true;
    public bool YearTag { get; set; } = true;
    public bool ReleaseDateTag { get; set; } = true;
    public bool ComposerTag { get; set; } = true;
    public bool CopyrightTag { get; set; } = true;
    public bool LabelTag { get; set; } = true;
    public bool IsrcTag { get; set; } = true;
    public bool UpcTag { get; set; } = true;
    public bool TypeTag { get; set; } = true;
    public bool ExplicitTag { get; set; } = true;
    public bool ImageTag { get; set; } = true;
    public bool UrlTag { get; set; } = true;
    public bool CommentTag { get; set; }
    public string CommentText { get; set; } = "";
    public bool MergeArtistNames { get; set; }
    public bool MergeArtistNamesInDirNames { get; set; }
    public bool ExplicitTagInTitle { get; set; }
    public string ExplicitTitleTagText { get; set; } = " (Explicit)";
    public int ExplicitTitleTagPrefixOrSuffix { get; set; } = 1; // 0=prefix, 1=suffix
    public bool CleanTagInTitle { get; set; }
    public string CleanTitleTagText { get; set; } = " (Clean)";
    public int CleanTitleTagPrefixOrSuffix { get; set; } = 1;

    // Artwork
    public string EmbeddedArtSize { get; set; } = "600";
    public string SavedArtSize { get; set; } = "600";
    public bool DontSaveArtworkToDisk { get; set; }

    // Advanced
    public bool StreamableCheck { get; set; } = true;
    public bool FixMD5s { get; set; }
    public bool DownloadGoodies { get; set; } = true;
    public bool DownloadSpeed { get; set; }
    public bool UseItemPosInPlaylist { get; set; }
    public bool LogFailedDownloads { get; set; } = true;
    public string DuplicateFileMode { get; set; } = "skip"; // skip, rename, overwrite
}
