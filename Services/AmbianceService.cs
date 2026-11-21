namespace QBDL.Web.Services;

/// <summary>
/// Pochettes du mur du fond (MainLayout.razor), propres à chaque circuit : chaque page fournit
/// les siennes (SetCovers) et les retire en partant (Clear). Le CSS les grise et les floute.
/// </summary>
public sealed class AmbianceService
{
    /// <summary>Au-delà, le mur n'en montre pas davantage : 8 colonnes × 6 rangées au bureau.</summary>
    public const int MaxCovers = 48;

    private IReadOnlyList<string> _covers = Array.Empty<string>();

    // Page qui a fourni la liste en cours. À la navigation, la nouvelle page est initialisée avant
    // que l'ancienne ne soit libérée : sans ce propriétaire, le Clear de l'ancienne effacerait les
    // pochettes que la nouvelle vient de poser.
    private object? _owner;

    public IReadOnlyList<string> Covers => _covers;

    public event Action? Changed;

    public void SetCovers(object owner, IEnumerable<string?>? urls)
    {
        _owner = owner;

        // Adresses absolues http(s) uniquement : la liste vient des réponses de l'API Qobuz, mais
        // finit dans un attribut src.
        var covers = (urls ?? Enumerable.Empty<string?>())
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            .Select(u => u!)
            .Distinct()
            .Take(MaxCovers)
            .ToArray();

        if (covers.SequenceEqual(_covers)) return;
        _covers = covers;
        Changed?.Invoke();
    }

    /// <summary>Retire le mur, seulement si c'est encore celui de cette page.</summary>
    public void Clear(object owner)
    {
        if (!ReferenceEquals(_owner, owner)) return;
        SetCovers(owner, null);
        _owner = null;
    }
}
