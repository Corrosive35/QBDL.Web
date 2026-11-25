using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using QBDL.Web.Models;

namespace QBDL.Web.Services;

public class QobuzApiService
{
    private readonly HttpClient _http;
    private readonly ILogger<QobuzApiService> _logger;
    private const string BaseUrl = "https://www.qobuz.com/api.json/0.2/";
    private const string PlayBaseUrl = "https://play.qobuz.com";

    private string? _appId;
    private string? _appSecret;
    private string? _userAuthToken;

    public string? AppId => _appId;
    public string? AppSecret => _appSecret;
    public string? UserAuthToken => _userAuthToken;
    public bool IsLoggedIn => !string.IsNullOrEmpty(_userAuthToken);

    public QobuzApiService(ILogger<QobuzApiService> logger)
    {
        _logger = logger;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public void SetCredentials(string appId, string appSecret, string userAuthToken)
    {
        _appId = appId;
        _appSecret = appSecret;
        _userAuthToken = userAuthToken;

        _http.DefaultRequestHeaders.Remove("X-App-Id");
        if (!string.IsNullOrEmpty(appId))
            _http.DefaultRequestHeaders.Add("X-App-Id", appId);
    }

    // ─── App ID / Secret Retrieval ───

    public async Task<string> GetAppIdAsync()
    {
        _logger.LogInformation("Fetching app_id from Qobuz...");
        var html = await _http.GetStringAsync($"{PlayBaseUrl}/login");
        var bundleMatch = Regex.Match(html, @"""(/resources/\d+\.\d+\.\d+-[a-z]\d+/bundle\.js)""");
        if (!bundleMatch.Success)
            throw new Exception("Could not find bundle.js URL in Qobuz page");

        var bundleUrl = PlayBaseUrl + bundleMatch.Groups[1].Value;
        var bundleJs = await _http.GetStringAsync(bundleUrl);

        _appId = ExtractAppId(bundleJs)
            ?? throw new Exception("Could not extract app_id from bundle.js");

        // Trouvé, sans le citer : aucun app_id ni app_secret dans les journaux.
        _logger.LogInformation("Got app_id from the Qobuz web player");
        return _appId;
    }

    // app_id du lecteur web : dans sa configuration de production, sinon sous sa forme ancienne.
    private static string? ExtractAppId(string bundleJs)
    {
        var match = Regex.Match(bundleJs, @"production:\{api:\{appId:""(\d+)"",appSecret:""(\w+)""");
        if (!match.Success)
            match = Regex.Match(bundleJs, @"app_id:\s*""(\d+)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Clés de la connexion Qobuz (OAuth) du lecteur web : son app_id, que la page de connexion de
    /// Qobuz attend en <c>ext_app_id</c>, et la clé privée qui échange le code de retour contre un
    /// jeton. Relues à chaque connexion parce que la clé change avec les versions du lecteur. Ne
    /// touche pas à l'état du service : une connexion lancée puis abandonnée ne doit rien changer.
    /// </summary>
    public async Task<(string AppId, string PrivateKey)> GetOAuthKeysAsync()
    {
        var bundleJs = await FetchBundleJsAsync();

        var appId = ExtractAppId(bundleJs)
            ?? throw new Exception("Could not extract app_id from bundle.js");

        // Le bundle contient d'autres « privateKey » (clé RSA de WebCrypto) : seule la forme
        // privateKey:"<alphanumérique>" est celle de l'échange OAuth. Jamais journalisée.
        var keyMatch = Regex.Match(bundleJs, @"privateKey:\s*""(?<key>[A-Za-z0-9]{6,30})""");
        if (!keyMatch.Success)
            throw new Exception("Could not extract the OAuth private key from bundle.js");

        return (appId, keyMatch.Groups["key"].Value);
    }

    public async Task<string> GetAppSecretAsync(string appId, string userAuthToken)
    {
        _logger.LogInformation("Fetching app_secret...");
        var bundleJs = await FetchBundleJsAsync();

        // ── Method 1: seed + info + extras ──
        var candidateSecrets = ExtractSecretsFromBundle(bundleJs);
        _logger.LogInformation("Found {Count} candidate secrets from bundle", candidateSecrets.Count);

        foreach (var secret in candidateSecrets)
        {
            if (await TestSecretAsync(appId, secret))
            {
                _appSecret = secret;
                _logger.LogInformation("Found valid app_secret via seed+info+extras");
                return _appSecret;
            }
        }

        // ── Method 2: Brute-force 32-char hex strings ──
        var hexMatches = Regex.Matches(bundleJs, @"['""]([a-f0-9]{32})['""]");
        var hexSeeds = hexMatches.Select(m => m.Groups[1].Value).Distinct().ToList();
        _logger.LogInformation("Found {Count} hex candidates from bundle", hexSeeds.Count);

        foreach (var seed in hexSeeds)
        {
            if (await TestSecretAsync(appId, seed))
            {
                _appSecret = seed;
                _logger.LogInformation("Found valid app_secret via hex brute-force");
                return _appSecret;
            }
        }

        // Pas de lecture directe du `appSecret:"…"` du bundle : Qobuz refuse ce secret, et la chaîne
        // figure de toute façon parmi les candidats hexadécimaux testés ci-dessus.
        throw new Exception("Could not determine app_secret");
    }

    private async Task<string> FetchBundleJsAsync()
    {
        var html = await _http.GetStringAsync($"{PlayBaseUrl}/login");

        var bundleMatch = Regex.Match(html, @"<script src=""(/resources/\d+\.\d+\.\d+-[a-z]\d{3}/bundle\.js)""");
        if (!bundleMatch.Success)
            bundleMatch = Regex.Match(html, @"""(/resources/\d+\.\d+\.\d+-[a-z]\d+/bundle\.js)""");
        if (!bundleMatch.Success)
            throw new Exception("Could not find bundle.js URL");

        var bundleUrl = PlayBaseUrl + bundleMatch.Groups[1].Value;
        _logger.LogInformation("Fetching bundle: {Url}", bundleUrl);
        return await _http.GetStringAsync(bundleUrl);
    }

    private List<string> ExtractSecretsFromBundle(string bundleJs)
    {
        var decoded = new List<string>();

        // ── Primary: Berlin-only extraction ──
        var berlinInfoMatch = Regex.Match(bundleJs,
            @"name:""(?<timezone>[A-Za-z\/]+\/Berlin)"",info:""(?<info>[\w=]+)"",extras:""(?<extras>[\w=]+)""");
        var berlinSeedMatch = Regex.Match(bundleJs,
            @"[a-z]\.initialSeed\(""(?<seed>[\w=]+)"",window\.utimezone\.(?<timezone>berlin)\)");

        if (berlinInfoMatch.Success && berlinSeedMatch.Success)
        {
            var info = berlinInfoMatch.Groups["info"].Value;
            var extras = berlinInfoMatch.Groups["extras"].Value;
            var seed = berlinSeedMatch.Groups["seed"].Value;

            // Seed, info et extras ne sont jamais journalisés : mis bout à bout, ils redonnent le secret.
            _logger.LogInformation("Found Berlin seed/info/extras in bundle");

            var combined = seed + info + extras;
            if (combined.Length > 44)
            {
                combined = combined.Remove(combined.Length - 44, 44);
                try
                {
                    var decodedBytes = Convert.FromBase64String(combined);
                    var secret = Encoding.UTF8.GetString(decodedBytes);
                    if (!string.IsNullOrEmpty(secret))
                    {
                        _logger.LogInformation("Decoded Berlin secret: {Len} chars", secret.Length);
                        decoded.Add(secret);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Berlin Base64 decode failed: {Error}", ex.Message);
                }
            }
        }
        else
        {
            _logger.LogWarning("Berlin seed/info/extras not found, trying all timezones...");
        }

        // ── Fallback: All timezones ──
        var secrets = new Dictionary<string, List<string>>();
        var seedRegex = new Regex(@"[a-z]\.initialSeed\(""(?<seed>[\w=]+)"",window\.utimezone\.(?<timezone>[a-z]+)\)");
        foreach (Match m in seedRegex.Matches(bundleJs))
        {
            var seed = m.Groups["seed"].Value;
            var tz = m.Groups["timezone"].Value;
            if (tz == "berlin") continue; // Already handled above
            secrets[tz] = new List<string> { seed };
        }

        if (secrets.Count > 0)
        {
            var timezones = string.Join("|", secrets.Keys.Select(tz => char.ToUpper(tz[0]) + tz[1..]));
            var infoRegex = new Regex($@"name:""\w+/(?<timezone>{timezones})"",info:""(?<info>[\w=]+)"",extras:""(?<extras>[\w=]+)""");

            foreach (Match m in infoRegex.Matches(bundleJs))
            {
                var tz = m.Groups["timezone"].Value.ToLower();
                if (secrets.ContainsKey(tz))
                {
                    secrets[tz].Add(m.Groups["info"].Value);
                    secrets[tz].Add(m.Groups["extras"].Value);
                }
            }

            foreach (var kvp in secrets)
            {
                if (kvp.Value.Count < 3) continue;
                var combined = string.Join("", kvp.Value);
                if (combined.Length <= 44) continue;
                combined = combined.Remove(combined.Length - 44, 44);
                try
                {
                    var decodedBytes = Convert.FromBase64String(combined);
                    var secret = Encoding.UTF8.GetString(decodedBytes);
                    if (!string.IsNullOrEmpty(secret))
                        decoded.Add(secret);
                }
                catch { }
            }
        }

        return decoded;
    }

    private async Task<bool> TestSecretAsync(string appId, string secret)
    {
        try
        {
            // Piste et format fixes : seule la validité de la signature est testée.
            var timestamp = ((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
            var sigData = $"trackgetFileUrlformat_id5intentstreamtrack_id197432204{timestamp}{secret}";
            var sig = ComputeMD5(sigData);

            var url = $"{BaseUrl}track/getFileUrl?request_ts={timestamp}&request_sig={sig}&track_id=197432204&format_id=5&intent=stream&app_id={appId}";

            // X-App-Id sur la requête : la connexion Qobuz (OAuth) cherche le secret avant que l'app_id
            // ne soit posé sur le client partagé, absent sur une installation neuve et retiré par
            // « Déconnecter Qobuz ». Sans lui, Qobuz pourrait refuser chaque candidat.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-App-Id", appId);
            using var resp = await _http.SendAsync(request);

            // A valid secret will NOT return 400 (Invalid Request Signature)
            // It may return 401/403 (auth issues) or 200 but NOT 400
            _logger.LogDebug("Test secret result: {Status}", (int)resp.StatusCode);
            return (int)resp.StatusCode != 400;
        }
        catch
        {
            return false;
        }
    }

    // ─── Authentication ───

    /// <summary>
    /// Échange le code de retour de la connexion Qobuz (<c>code_autorisation</c>) contre un jeton,
    /// comme le fait le lecteur web : GET oauth/callback avec ce code et la clé privée lue dans son
    /// bundle. Le jeton obtenu vaut, pour cet app_id, un jeton collé à la main. Ne touche pas à
    /// l'état du service : c'est l'enregistrement de la connexion qui l'adopte.
    /// </summary>
    /// <remarks>
    /// La connexion par e-mail et mot de passe (user/login) a disparu : depuis avril 2026 environ,
    /// Qobuz la refuse aux applications tierces, identifiants justes ou non (401 « User
    /// authentication is required. »).
    /// </remarks>
    public async Task<string> ExchangeOAuthCodeAsync(string appId, string privateKey, string code, CancellationToken ct = default)
    {
        // Cette URL porte le code et la clé privée : ni elle ni eux ne sont journalisés ni repris
        // dans une exception.
        var url = $"{BaseUrl}oauth/callback?code={Uri.EscapeDataString(code)}&private_key={Uri.EscapeDataString(privateKey)}&app_id={appId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Sur la requête et non sur le client partagé : l'app_id en service ne doit changer qu'une
        // fois la connexion enregistrée.
        request.Headers.Add("X-App-Id", appId);
        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Code et message de Qobuz seulement, jamais le corps brut.
            var error = ParseApiError(json);
            _logger.LogWarning("Qobuz refused oauth/callback (HTTP {Status}): code {Code}, {Message}",
                (int)response.StatusCode, error?.Code, error?.Message);
            // Le code HTTP porté par l'exception distingue un refus de Qobuz d'une panne réseau.
            throw new HttpRequestException($"oauth/callback failed ({(int)response.StatusCode}): {error?.Message}", null, response.StatusCode);
        }

        string? token = null;
        try { token = JsonConvert.DeserializeObject<OAuthCallbackResponse>(json)?.Token; }
        catch (JsonException) { }

        return string.IsNullOrEmpty(token)
            ? throw new Exception("oauth/callback returned no token")
            : token;
    }

    public async Task<QobuzLoginResponse> LoginWithTokenAsync(string appId, string token)
    {
        _appId = appId;
        _userAuthToken = token;

        // Ensure X-App-Id header is set (may have been cleared by logout)
        _http.DefaultRequestHeaders.Remove("X-App-Id");
        _http.DefaultRequestHeaders.Add("X-App-Id", appId);

        // Validate token by fetching user info
        var user = await GetUserInfoAsync(appId, token);
        _logger.LogInformation("Logged in with token as {Name}", user?.DisplayName);
        return new QobuzLoginResponse
        {
            UserAuthToken = token,
            User = user
        };
    }

    /// <summary>
    /// Fiche du compte : identité, droits de streaming, abonnement. Seul appel qui renseigne
    /// l'abonnement sans télécharger, quel que soit le mode de connexion : il détecte un abonnement
    /// expiré avant la première piste. Sans abonnement, la réponse est 200 avec
    /// <c>credential.parameters</c> à null.
    /// </summary>
    public async Task<QobuzUserInfo?> GetUserInfoAsync(string appId, string token, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}user/get?app_id={appId}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-User-Auth-Token", token);
        // Sur la requête : la connexion Qobuz (OAuth) valide son jeton avant que l'app_id ne soit
        // posé sur le client partagé. Il prime alors sur celui du client, qui n'est pas dupliqué.
        request.Headers.Add("X-App-Id", appId);
        var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("user/get failed ({Status}): {Body}", response.StatusCode, json);
            // HttpRequestException *avec* le code HTTP : la page de connexion distingue ainsi un jeton
            // refusé (4xx) d'une panne réseau (pas de code). Les autres appelants interceptent
            // Exception et ne voient pas la différence.
            throw new HttpRequestException($"Token login failed ({(int)response.StatusCode}): {json}", null, response.StatusCode);
        }

        return JsonConvert.DeserializeObject<QobuzUserInfo>(json);
    }

    // ─── Album ───

    public async Task<QobuzAlbum?> GetAlbumAsync(string albumId, int limit = 500, int offset = 0)
    {
        var url = $"{BaseUrl}album/get?album_id={albumId}&app_id={_appId}&limit={limit}&offset={offset}";
        return await AuthGetAsync<QobuzAlbum>(url);
    }

    public async Task<QobuzAlbum> GetAlbumFullAsync(string albumId)
    {
        var album = await GetAlbumAsync(albumId)
            ?? throw new Exception($"Album {albumId} not found");

        if (album.Tracks != null && album.Tracks.Total > album.Tracks.Items.Count)
        {
            int offset = album.Tracks.Items.Count;
            while (offset < album.Tracks.Total)
            {
                var page = await GetAlbumAsync(albumId, 500, offset);
                if (page?.Tracks?.Items == null || page.Tracks.Items.Count == 0) break;
                album.Tracks.Items.AddRange(page.Tracks.Items);
                offset += page.Tracks.Items.Count;
                if (offset > 100000) break;
            }
        }
        return album;
    }

    // ─── Track ───

    public async Task<QobuzTrack?> GetTrackAsync(string trackId)
    {
        var url = $"{BaseUrl}track/get?track_id={trackId}&app_id={_appId}";
        return await AuthGetAsync<QobuzTrack>(url);
    }

    public async Task<QobuzStreamUrl?> GetStreamUrlAsync(string trackId, string formatId)
    {
        if (string.IsNullOrEmpty(_appSecret))
            throw new Exception("App secret not set");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sigData = $"trackgetFileUrlformat_id{formatId}intentstreamtrack_id{trackId}{timestamp}{_appSecret}";
        var sig = ComputeMD5(sigData);

        // app_id et user_auth_token en paramètres de requête, comme dans l'application d'origine.
        var url = $"{BaseUrl}track/getFileUrl?request_ts={timestamp}&request_sig={sig}&track_id={trackId}&format_id={formatId}&intent=stream&app_id={_appId}&user_auth_token={_userAuthToken}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Stream URL error {Code}: {Body}", (int)response.StatusCode, json);
            throw new Exception($"Qobuz API error {(int)response.StatusCode}: {json}");
        }

        return JsonConvert.DeserializeObject<QobuzStreamUrl>(json);
    }

    // ─── Search ───

    public async Task<QobuzSearchAlbumResult?> SearchAlbumsAsync(string query, int limit = 50, int offset = 0)
    {
        var url = $"{BaseUrl}album/search?query={Uri.EscapeDataString(query)}&app_id={_appId}&limit={limit}&offset={offset}";
        return await AuthGetAsync<QobuzSearchAlbumResult>(url);
    }

    public async Task<QobuzSearchTrackResult?> SearchTracksAsync(string query, int limit = 50, int offset = 0)
    {
        var url = $"{BaseUrl}track/search?query={Uri.EscapeDataString(query)}&app_id={_appId}&limit={limit}&offset={offset}";
        return await AuthGetAsync<QobuzSearchTrackResult>(url);
    }

    public async Task<QobuzSearchArtistResult?> SearchArtistsAsync(string query, int limit = 50, int offset = 0)
    {
        var url = $"{BaseUrl}artist/search?query={Uri.EscapeDataString(query)}&app_id={_appId}&limit={limit}&offset={offset}";
        return await AuthGetAsync<QobuzSearchArtistResult>(url);
    }

    // ─── Playlist ───

    public async Task<QobuzPlaylist?> GetPlaylistAsync(string playlistId, int limit = 500, int offset = 0)
    {
        var url = $"{BaseUrl}playlist/get?playlist_id={playlistId}&app_id={_appId}&extra=tracks&limit={limit}&offset={offset}";
        return await AuthGetAsync<QobuzPlaylist>(url);
    }

    public async Task<QobuzPlaylist> GetPlaylistFullAsync(string playlistId)
    {
        var playlist = await GetPlaylistAsync(playlistId)
            ?? throw new Exception($"Playlist {playlistId} not found");

        if (playlist.Tracks != null && playlist.Tracks.Total > playlist.Tracks.Items.Count)
        {
            int offset = playlist.Tracks.Items.Count;
            while (offset < playlist.Tracks.Total)
            {
                var page = await GetPlaylistAsync(playlistId, 500, offset);
                if (page?.Tracks?.Items == null || page.Tracks.Items.Count == 0) break;
                playlist.Tracks.Items.AddRange(page.Tracks.Items);
                offset += page.Tracks.Items.Count;
                if (offset > 1000000) break;
            }
        }
        return playlist;
    }

    // ─── Artist ───

    public async Task<QobuzArtist?> GetArtistAsync(string artistId, int limit = 500, int offset = 0)
    {
        var url = $"{BaseUrl}artist/get?artist_id={artistId}&app_id={_appId}&extra=albums%2Calbums_with_last_release&limit={limit}&offset={offset}";
        return await AuthGetAsync<QobuzArtist>(url);
    }

    public async Task<QobuzArtist> GetArtistFullAsync(string artistId)
    {
        var artist = await GetArtistAsync(artistId)
            ?? throw new Exception($"Artist {artistId} not found");

        if (artist.Albums != null && artist.Albums.Total > artist.Albums.Items.Count)
        {
            int offset = artist.Albums.Items.Count;
            while (offset < artist.Albums.Total)
            {
                var page = await GetArtistAsync(artistId, 500, offset);
                if (page?.Albums?.Items == null || page.Albums.Items.Count == 0) break;
                artist.Albums.Items.AddRange(page.Albums.Items);
                offset += page.Albums.Items.Count;
                if (offset > 100000) break;
            }
        }
        return artist;
    }

    // ─── Label ───

    public async Task<QobuzLabel?> GetLabelAsync(string labelId, int limit = 500, int offset = 0)
    {
        var url = $"{BaseUrl}label/get?label_id={labelId}&app_id={_appId}&extra=albums&limit={limit}&offset={offset}";
        return await AuthGetAsync<QobuzLabel>(url);
    }

    // ─── Favorites ───

    public async Task<QobuzFavorites?> GetFavoritesAsync(string userId, string type, int limit = 500, int offset = 0)
    {
        var url = $"{BaseUrl}favorite/getUserFavorites?user_id={userId}&app_id={_appId}&type={type}&limit={limit}&offset={offset}";
        return await AuthGetAsync<QobuzFavorites>(url);
    }

    // ─── Helpers ───

    private async Task<T?> AuthGetAsync<T>(string url) where T : class
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_userAuthToken))
            request.Headers.Add("X-User-Auth-Token", _userAuthToken);

        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("API error {Code}: {Body}", (int)response.StatusCode, json);
            throw new Exception($"Qobuz API error {(int)response.StatusCode}: {json}");
        }

        return JsonConvert.DeserializeObject<T>(json);
    }

    // Réponse d'erreur de l'API : {"status":"error","code":401,"message":"…"}. Null si le corps n'est
    // pas de ce format (page HTML d'une passerelle, corps vide…).
    private static QobuzApiError? ParseApiError(string body)
    {
        try { return JsonConvert.DeserializeObject<QobuzApiError>(body); }
        catch (JsonException) { return null; }
    }

    private sealed class QobuzApiError
    {
        [JsonProperty("code")] public int? Code { get; set; }
        [JsonProperty("message")] public string? Message { get; set; }
    }

    private sealed class OAuthCallbackResponse
    {
        [JsonProperty("token")] public string? Token { get; set; }
    }

    private static string ComputeMD5(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLower();
    }
}
