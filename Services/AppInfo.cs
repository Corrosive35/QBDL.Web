using System.Reflection;

namespace QBDL.Web.Services;

/// <summary>Version affichée par la barre latérale et par /healthz.</summary>
public static class AppInfo
{
    /// <summary>
    /// Version informative : &lt;Version&gt; du csproj (« 1.8.0 »), sans hash de commit grâce à
    /// IncludeSourceRevisionInInformationalVersion. Seul le workflow de dev la remplace, via
    /// l'argument APP_VERSION du Dockerfile (« 1.8.0-dev+abc1234 »). QBDL.Remote lit ce champ comme
    /// du texte.
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
