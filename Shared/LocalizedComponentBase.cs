using Microsoft.AspNetCore.Components;
using QBDL.Web.Services;

namespace QBDL.Web.Shared;

/// <summary>
/// Base des pages traduites : met <see cref="Loc"/> à disposition.
/// </summary>
/// <remarks>
/// Elle s'abonnait autrefois au changement de langue pour redessiner la page sans la recharger. La
/// langue étant désormais propre à chaque utilisateur et fixée par requête, la bascule recharge la
/// page (voir LanguageSwitch) et il n'y a plus rien à écouter. Dispose reste virtuelle : les pages
/// qui s'abonnent à d'autres événements la surchargent et appellent base.Dispose().
/// </remarks>
public abstract class LocalizedComponentBase : ComponentBase, IDisposable
{
    [Inject] protected LocalizationService Loc { get; set; } = default!;

    public virtual void Dispose()
    {
    }
}
