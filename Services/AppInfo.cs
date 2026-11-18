using System.Reflection;

namespace QBDL.Web.Services;

/// <summary>
/// Version affichée par l'application : barre latérale (MainLayout.razor) et sonde /healthz
/// (Program.cs) lisent toutes deux cette valeur.
/// </summary>
public static class AppInfo
{
    /// <summary>
    /// Version informative de l'assembly. Sans argument de build, elle vaut &lt;Version&gt; du
    /// csproj (« 1.8.0 ») : IncludeSourceRevisionInInformationalVersion empêche le SDK d'y
    /// accoler le hash du commit. Seul le workflow de la branche dev la remplace, par l'argument
    /// APP_VERSION du Dockerfile (« 1.8.0-dev+abc1234 »). QBDL.Remote lit ce champ comme du texte.
    /// </summary>
    public static string Version { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        return assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    }
}
