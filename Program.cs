using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.StaticFiles;
using QBDL.Web.Models;
using QBDL.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Le trousseau Data Protection chiffre le cookie d'accès. Par défaut il irait dans le HOME du
// compte de l'image, hors volume : chaque redémarrage du conteneur régénérerait les clés et
// invaliderait sans trace tous les cookies émis.
//
// Le nom d'application sert lui aussi à chiffrer : le changer déconnecte chaque appareil,
// application Android comprise. Passé de « QobuzDLX.Web » à « QBDL.Web » en 1.7.0, il ne doit
// plus changer, même si le projet est renommé à nouveau.
var configPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "/config";
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(configPath, "dataprotection-keys")))
    .SetApplicationName("QBDL.Web");

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(options =>
{
    // Trois minutes par défaut ; dix couvrent « je réponds à un message et je reviens » sur mobile.
    // La rétention ne sert que si la même page se reconnecte : quand le système tue le navigateur,
    // c'est le rechargement automatique côté client qui prend le relais.
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
    options.DisconnectedCircuitMaxRetained = 50;
    // Le gel de l'onglet ne doit pas faire expirer une interopérabilité JS en vol.
    options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(1);
})
.AddHubOptions(options =>
{
    // Règle SignalR : le délai d'expiration vaut au moins deux fois le keep-alive. Le client a ses
    // propres valeurs (30 s / 15 s) et couperait le premier : leur pendant est dans _Host.cshtml.
    options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<QobuzApiService>();
// Singleton : les connexions Qobuz en cours (state) doivent survivre au passage du WebView ou de
// l'onglet qui les a lancées vers le navigateur qui revient de qobuz.com.
builder.Services.AddSingleton<QobuzSignInService>();
builder.Services.AddSingleton<TemplateService>();
builder.Services.AddSingleton<TaggingService>();
builder.Services.AddSingleton<DownloadService>();
builder.Services.AddSingleton<SearchStateStore>();
// Singleton : il ne porte que les catalogues. La langue, propre à chaque utilisateur, est fixée par
// requête par le middleware de localisation plus bas.
builder.Services.AddSingleton<LocalizationService>();
builder.Services.AddScoped<AppGuardService>();
// Scoped : le mur de pochettes du fond suit la page affichée dans chaque onglet.
builder.Services.AddScoped<AmbianceService>();
// Étranglement du déverrouillage : singleton, pour que le compteur ne soit pas remis à zéro
// par l'ouverture d'un nouvel onglet.
builder.Services.AddSingleton<AppAccessGate>();

// Cookie d'accès : un état gardé dans le circuit Blazor se perdrait à chaque rechargement, donc à
// chaque retour dans une WebView que le système a gelée puis tuée.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "qbdlx_access";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.Path = "/";
        // Lax et non None : le cookie n'a besoin de voyager que sur les navigations de premier
        // niveau, et None imposerait le drapeau Secure — impossible à garantir en HTTP sur une
        // adresse privée (VPN, réseau local).
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Derrière un reverse proxy qui termine TLS, Kestrel ne voit que du HTTP : le drapeau
        // Secure ne sera donc pas posé. Le récupérer imposerait UseForwardedHeaders sans liste
        // de proxys connus (leur adresse est dynamique en Docker), c'est-à-dire faire confiance
        // au X-Forwarded-Proto de quiconque peut joindre Kestrel. Le jeu n'en vaut pas la chandelle.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;

        // Aucun endpoint n'est [Authorize] : ces redirections vers une page de connexion qui
        // n'existe pas ne doivent jamais se déclencher.
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };

        // Le cookie fige le compte et l'empreinte du mot de passe pour lequel il a été émis. Changer ce
        // mot de passe déconnecte donc tous les appareils de ce compte — et d'eux seuls —, supprimer
        // le compte tous ses appareils. Un cookie 1.6, sans identifiant de compte, est rejeté.
        options.Events.OnValidatePrincipal = ctx =>
        {
            var users = ctx.HttpContext.RequestServices.GetRequiredService<UserStore>();
            var user = users.Find(ctx.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value);

            if (user != null
                && ctx.Principal?.FindFirst(AppAccessGate.StampClaim)?.Value == AppAccessGate.StampFor(user.PasswordHash))
                return Task.CompletedTask;

            ctx.RejectPrincipal();
            return ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        };
    });

// AddServerSideBlazor() l'enregistre déjà via TryAdd ; l'écrire explicitement documente la
// dépendance de AppGuardService et met la résolution à l'abri d'un changement d'implémentation.
builder.Services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();

var app = builder.Build();

// Le tout premier : c'est lui qui migre une installation 1.6 vers les comptes, en relisant dans
// settings.json des champs qu'AppSettings ne déclare plus. Tout enregistrement de settings.json
// avant lui les effacerait.
var userStore = app.Services.GetRequiredService<UserStore>();

// Instancié dès le démarrage : charge les catalogues et signale dans les logs toute clé de
// traduction manquante avant la première requête.
app.Services.GetRequiredService<LocalizationService>();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// .webmanifest est absent de la table MIME par défaut : sans ce mapping le fichier partirait en
// application/octet-stream et Chrome ignorerait le manifeste, donc l'installation sur l'écran
// d'accueil, sans rien signaler.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

app.UseRouting();

// Après UseRouting et avant tout endpoint : c'est ce qui donne un Context.User au handshake
// du hub Blazor, donc à la CircuitFactory qui amorce l'état d'authentification du circuit.
app.UseAuthentication();

// Langue de chaque requête : celle du compte connecté, sinon le cookie, sinon le navigateur. Après
// UseAuthentication, qui fournit le compte. La requête qui ouvre le circuit Blazor passe aussi par
// ici, et le circuit garde cette culture : chaque utilisateur voit l'interface dans sa langue.
var supportedCultures = new List<CultureInfo>
{
    new(LocalizationService.CultureNameFor(LocalizationService.French)),
    new(LocalizationService.CultureNameFor(LocalizationService.English)),
};
app.UseRequestLocalization(options =>
{
    options.DefaultRequestCulture = new RequestCulture(supportedCultures[0]);
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    options.RequestCultureProviders.Clear();
    options.RequestCultureProviders.Add(new CustomRequestCultureProvider(ctx =>
    {
        var lang = LocalizationService.ResolveLanguage(ctx, userStore);
        return Task.FromResult<ProviderCultureResult?>(
            new ProviderCultureResult(LocalizationService.CultureNameFor(lang)));
    }));
});

// Sonde utilisée par l'application Android pour valider qu'une URL saisie pointe bien sur une
// instance QBDL.Web avant de l'enregistrer. Volontairement non authentifiée et sans donnée
// sensible : le nom de l'application et sa version suffisent à lever l'ambiguïté. Avant la
// 1.7.0, la sonde répondait « QobuzDLX.Web » : QBDL.Remote accepte les deux noms.
app.MapGet("/healthz", () => Results.Json(new
{
    app = "QBDL.Web",
    version = AppInfo.Version
}));

// Déverrouillage par formulaire HTML classique, et non par un gestionnaire Blazor : poser un
// cookie exige une vraie réponse HTTP, et l'écran de verrouillage devient utilisable dès le
// prérendu, sans attendre l'établissement du circuit.
//
// Le chemin date de l'époque du mot de passe unique ; il est conservé parce que l'application
// Android (QBDL.Remote) en dépend.
app.MapPost("/auth/unlock", async (HttpContext ctx, UserStore users, AppAccessGate gate) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = LocalPath(form["returnUrl"].ToString());

    // Aucun compte encore : c'est l'écran de création de l'administrateur qui s'affichera.
    if (!users.HasUsers)
        return Results.Redirect(returnUrl);

    if (gate.IsLockedOut)
        return Results.Redirect($"{returnUrl}?lock=throttled");

    var user = users.Authenticate(username, password);
    if (user == null)
    {
        gate.RegisterFailure();
        return Results.Redirect($"{returnUrl}?lock={(gate.IsLockedOut ? "throttled" : "bad")}");
    }

    gate.RegisterSuccess();

    // Ancien hachage SHA256 sans sel : seule occasion de le repasser en PBKDF2, puisque c'est le seul
    // moment où le mot de passe est connu en clair. Fait avant l'émission du cachet (« user » est
    // l'objet que modifie Update), sinon OnValidatePrincipal rejetterait le cookie dès la requête
    // suivante. Les autres appareils du compte redemanderont le mot de passe une fois.
    if (AppGuardService.IsLegacyHash(user.PasswordHash))
    {
        var upgraded = AppGuardService.HashPassword(password);
        users.Update(user.Id, u => u.PasswordHash = upgraded);
    }

    AdoptRequestLanguage(ctx, users, user);
    await SignInUserAsync(ctx, user);
    return Results.Redirect(returnUrl);
});

// Création du premier administrateur. N'aboutit que tant qu'aucun compte n'existe : UserStore
// vérifie et crée sous le même verrou.
app.MapPost("/auth/setup", async (HttpContext ctx, UserStore users) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var confirm = form["confirm"].ToString();

    if (users.HasUsers)
        return Results.Redirect("/");
    if (password != confirm)
        return Results.Redirect("/?setup=setup.error.mismatch");

    var (user, error) = users.CreateFirstAdmin(username, password);
    if (user == null)
        return Results.Redirect($"/?setup={error}");

    AdoptRequestLanguage(ctx, users, user);
    await SignInUserAsync(ctx, user);
    return Results.Redirect("/");
});

// Changement de son propre mot de passe. Un formulaire et non un gestionnaire Blazor, comme le
// déverrouillage : le hachage change, donc l'empreinte, et seule une vraie réponse HTTP peut
// réémettre le cookie. Sans cela l'appareil courant serait déconnecté avec les autres.
app.MapPost("/auth/password", async (HttpContext ctx, UserStore users, AppAccessGate gate) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var current = form["current"].ToString();
    var next = form["password"].ToString();
    var confirm = form["confirm"].ToString();

    // SameSite=Lax : un POST venu d'un autre site n'a pas de cookie et s'arrête ici.
    var user = users.Find(ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    if (user == null)
        return Results.Redirect("/");

    if (gate.IsLockedOut)
        return Results.Redirect("/settings?pwd=throttled");

    // Compté comme un échec de connexion : une session laissée ouverte ne doit pas permettre
    // d'éprouver le mot de passe à volonté.
    if (!AppGuardService.VerifyPassword(current, user.PasswordHash))
    {
        gate.RegisterFailure();
        return Results.Redirect($"/settings?pwd={(gate.IsLockedOut ? "throttled" : "bad")}");
    }

    if (next != confirm)
        return Results.Redirect("/settings?pwd=mismatch");
    if (users.SetPassword(user.Id, next) != null)
        return Results.Redirect("/settings?pwd=empty");

    // « user » est l'objet que SetPassword vient de modifier : le cachet porte le nouveau hachage.
    await SignInUserAsync(ctx, user);
    return Results.Redirect("/settings?pwd=ok");
});

app.MapPost("/auth/lock", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

// Bascule de langue : enregistrée sur le compte si l'on est connecté, sinon dans le seul cookie
// de ce navigateur.
app.MapPost("/culture", async (HttpContext ctx, UserStore users) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var lang = form["lang"].ToString();
    var returnUrl = LocalPath(form["returnUrl"].ToString());

    if (lang is LocalizationService.French or LocalizationService.English)
    {
        LocalizationService.WriteCookie(ctx.Response, lang, ctx.Request.IsHttps);

        var user = users.Find(ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        if (user != null)
            users.Update(user.Id, u => u.UiLanguage = lang);
    }

    return Results.Redirect(returnUrl);
});

// Retour de la connexion Qobuz : qobuz.com y renvoie le navigateur avec le state posé au départ et
// le code à échanger (code_autorisation, nom choisi par Qobuz). Route publique, sans cookie : dans
// l'application Android, ce retour arrive dans le navigateur externe. C'est le state qui l'autorise
// (QobuzSignInService). L'écran de verrouillage ne concerne que les pages Blazor : aucune route
// minimale n'a besoin d'en être exemptée.
app.MapGet(QobuzSignInService.CallbackPath, async (HttpContext ctx, QobuzSignInService signIn, LocalizationService loc) =>
{
    var query = ctx.Request.Query;
    var code = query["code_autorisation"].ToString();
    if (string.IsNullOrEmpty(code))
        code = query["code"].ToString();

    var result = await signIn.CompleteAsync(query["state"].ToString(), code);

    // L'adresse de cette page porte le state et le code. Consommés, ils restent inutiles à
    // divulguer : ni cache, ni Referer vers la page suivante, redirection comprise.
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";

    // Navigateur déjà connecté à QBDL (cookie d'accès validé par UseAuthentication) : retour direct
    // dans l'application, dont la barre latérale nomme le compte Qobuz. Jamais pour une connexion
    // lancée depuis l'application Android : son retour arrive dans le navigateur du téléphone, et
    // c'est vers l'application qu'il faut revenir, cookie ou non (QobuzSignInPage). Sans cookie,
    // la page de retour nomme le compte. Une erreur s'affiche toujours.
    if (result.Outcome == QobuzSignInService.Outcome.Connected && !result.FromApp
        && ctx.User.Identity?.IsAuthenticated == true)
        return Results.Redirect("/");

    return Results.Content(QobuzSignInPage(loc, result), "text/html; charset=utf-8");
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

// Le cookie porte l'identifiant du compte, pas son nom : l'administrateur peut le renommer sans
// déconnecter personne. Le cachet fige le hachage du mot de passe (voir OnValidatePrincipal).
static Task SignInUserAsync(HttpContext ctx, UserAccount user)
{
    var identity = new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(AppAccessGate.StampClaim, AppAccessGate.StampFor(user.PasswordHash)),
        },
        CookieAuthenticationDefaults.AuthenticationScheme);

    // IsPersistent : sans lui le cookie meurt avec la session du navigateur, c'est-à-dire dès
    // qu'Android tue le processus du WebView — exactement ce qu'on cherche à éviter.
    return ctx.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true });
}

// À la connexion : un compte qui n'a jamais choisi de langue adopte celle de l'écran qu'il vient de
// voir (cookie ou navigateur), plutôt que de basculer en français. Le cookie reprend ensuite celle du
// compte, pour que l'écran de connexion suivant, sur ce navigateur, parle la même langue.
static void AdoptRequestLanguage(HttpContext ctx, UserStore users, UserAccount user)
{
    if (string.IsNullOrEmpty(user.UiLanguage))
    {
        var lang = LocalizationService.Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        users.Update(user.Id, u => u.UiLanguage = lang);
    }

    LocalizationService.WriteCookie(ctx.Response, user.UiLanguage, ctx.Request.IsHttps);
}

// Page de retour de la connexion Qobuz. Autonome, sans circuit Blazor, puisqu'elle s'ouvre souvent
// dans un navigateur externe ; habillée comme l'écran de connexion. Sa langue est celle de la
// requête (cookie qbdlx_lang ou navigateur), fixée par le middleware de localisation.
static string QobuzSignInPage(LocalizationService loc, QobuzSignInService.SignInResult result)
{
    var connected = result.Outcome == QobuzSignInService.Outcome.Connected;
    var named = !string.IsNullOrWhiteSpace(result.AccountName);
    var message = result.Outcome switch
    {
        // Le nom du compte : qobuz.com redonne sans rien demander celui déjà ouvert dans le
        // navigateur, et un changement de compte doit pouvoir se vérifier d'un coup d'œil.
        QobuzSignInService.Outcome.Connected when result.FromApp
            => named ? loc.T("qobuzSignIn.connectedApp", result.AccountName) : loc["qobuzSignIn.connectedAppNoName"],
        QobuzSignInService.Outcome.Connected when named
            => loc.T("qobuzSignIn.connectedAs", result.AccountName),
        QobuzSignInService.Outcome.Connected => loc["qobuzSignIn.connected"],
        QobuzSignInService.Outcome.InvalidState => loc["qobuzSignIn.error.state"],
        QobuzSignInService.Outcome.NotAdmin => loc["qobuzSignIn.error.notAdmin"],
        QobuzSignInService.Outcome.Unreachable => loc["qobuzSignIn.error.network"],
        QobuzSignInService.Outcome.AppCredentials => loc["qobuzSignIn.error.appCredentials"],
        _ => loc["qobuzSignIn.error.refused"],
    };
    static string E(string text) => System.Net.WebUtility.HtmlEncode(text);

    var notice = connected
        ? $"<div class=\"form-success\" role=\"status\">{E(message)}</div>"
        : $"<div class=\"auth-error\" role=\"alert\">{E(message)}</div>";

    // Suite proposée. Depuis un navigateur : ouvrir QBDL. Depuis l'application Android : y revenir,
    // par le lien qui la rouvre si elle le connaît (QBDLReturn/1), sinon par une simple consigne ;
    // « Ouvrir QBDL » mènerait à l'écran de connexion du navigateur, sans cookie.
    var action = !result.FromApp
        ? $"<a class=\"btn btn-primary\" href=\"/\" style=\"width:100%;text-decoration:none;\">{E(loc["qobuzSignIn.open"])}</a>"
        : result.AppCanReturn
            ? $"<a class=\"btn btn-primary\" href=\"{E(QobuzSignInService.AppReturnLink)}\" style=\"width:100%;text-decoration:none;\">{E(loc["qobuzSignIn.backToApp"])}</a>"
            : $"<p class=\"auth-subtitle\" style=\"text-align:center;\">{E(loc["qobuzSignIn.returnToApp"])}</p>";

    // Succès depuis une application qui sait se rouvrir : tentative automatique au chargement.
    // Chrome peut exiger un geste pour ouvrir une application, d'où le bouton ci-dessus. Lien fixe,
    // sans donnée de la requête ; aucune CSP ne restreint les scripts en ligne.
    var autoReturn = connected && result.FromApp && result.AppCanReturn
        ? $"<script>window.location.href = \"{QobuzSignInService.AppReturnLink}\";</script>"
        : "";

    return $"""
        <!DOCTYPE html>
        <html lang="{loc.Current}">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0, viewport-fit=cover" />
            <meta name="referrer" content="no-referrer" />
            <title>{E(loc["qobuzSignIn.title"])} · QBDL</title>
            <link rel="icon" href="/favicon.svg" type="image/svg+xml" />
            <link rel="stylesheet" href="/css/app.css" />
        </head>
        <body>
            <div class="auth-shell">
                <div class="auth-card">
                    <div class="card">
                        <div class="auth-head">
                            <h2 class="auth-logo">QBDL</h2>
                            <p class="auth-subtitle">{E(loc["qobuzSignIn.title"])}</p>
                        </div>
                        {notice}
                        {action}
                    </div>
                </div>
            </div>
            {autoReturn}
        </body>
        </html>
        """;
}

// Le formulaire renvoie la page d'où venait l'utilisateur ; ne garder qu'un chemin local ferme
// la redirection ouverte. Le rejet de « // » écarte les URL protocol-relative, celui de « : »
// les schémas absolus, et celui de « /\ » la même attaque déguisée : les navigateurs
// normalisent l'antislash en barre oblique, si bien qu'un Location « /\exemple.com » part bel et
// bien hors du site. Mêmes tests que Url.IsLocalUrl, dont on ne dispose pas hors contrôleur.
static string LocalPath(string? candidate) =>
    !string.IsNullOrEmpty(candidate)
    && candidate.StartsWith('/')
    && !candidate.StartsWith("//")
    && !candidate.StartsWith("/\\")
    && !candidate.Contains(':')
        ? candidate
        : "/";
