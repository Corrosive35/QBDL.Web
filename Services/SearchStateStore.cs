using System.Collections.Concurrent;

namespace QBDL.Web.Services;

/// <summary>
/// Dernière recherche de chaque utilisateur. Singleton pour la même raison que l'était
/// SearchStateService : survivre à un changement de page et au rechargement qui suit le gel d'un
/// onglet sur mobile. Mais un état par compte : un singleton unique montrait à chacun la recherche
/// du dernier qui avait cherché.
/// </summary>
public sealed class SearchStateStore
{
    private readonly ConcurrentDictionary<string, SearchStateService> _states = new();

    public SearchStateService For(string userId) => _states.GetOrAdd(userId, _ => new SearchStateService());

    /// <summary>À la suppression d'un compte.</summary>
    public void Remove(string userId) => _states.TryRemove(userId, out _);
}
