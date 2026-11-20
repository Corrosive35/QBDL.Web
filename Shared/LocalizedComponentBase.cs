using Microsoft.AspNetCore.Components;
using QBDL.Web.Services;

namespace QBDL.Web.Shared;

/// <summary>
/// Base des pages traduites : met <see cref="Loc"/> à disposition. Dispose reste virtuelle : les
/// pages abonnées à d'autres événements la surchargent et appellent base.Dispose().
/// </summary>
public abstract class LocalizedComponentBase : ComponentBase, IDisposable
{
    [Inject] protected LocalizationService Loc { get; set; } = default!;

    public virtual void Dispose()
    {
    }
}
