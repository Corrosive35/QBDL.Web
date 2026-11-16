namespace QBDL.Web.Models;

/// <summary>Un compte de l'application. Tous partagent le même compte Qobuz.</summary>
public class UserAccount
{
    // Clé stable : c'est elle que portent le cookie et les éléments de la file. L'identifiant de
    // connexion, lui, peut être renommé par l'administrateur sans rien casser.
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }

    // "" = pas encore choisie : la langue suit alors le cookie, puis le navigateur.
    public string UiLanguage { get; set; } = "";

    public UserSettings Settings { get; set; } = new();
}
