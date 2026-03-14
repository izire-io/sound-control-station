using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

/// <summary>
/// Handles Azure AD B2C + Bose authentication with local token caching and
/// automatic refresh. Token priority:
///   1. Valid cached token (not yet expiring)
///   2. Bose refresh token  (direct Bose refresh)
///   3. Azure refresh token (re-exchange id_token for Bose token)
///   4. Full PKCE auth flow (email + password)
/// </summary>
public sealed class BoseAuthenticator
{
    // ── Auth constants ────────────────────────────────────────────────────────
    private const string BoseApiKey  = "67616C617061676F732D70726F642D6D61647269642D696F73";
    private const string ClientId    = "e284648d-3009-47eb-8e74-670c5330ae54";
    private const string Policy      = "B2C_1A_MBI_SUSI";
    private const string Tenant      = "boseprodb2c.onmicrosoft.com";
    private const string BaseUrl     = "https://myboseid.bose.com";
    private const string RedirectUri = "bosemusic://auth/callback";
    private const string Scope       = $"openid email profile offline_access {ClientId}";
    private const string BoseTokenUrl = "https://id.api.bose.io/id-jwt-core/idps/aad/B2C_1A_MBI_SUSI/token";

    // ── Token cache file ──────────────────────────────────────────────────────
    private static readonly string CacheFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "sound-control-station",
        "token-cache.json");

    private readonly string _email;
    private readonly string _password;

    public BoseAuthenticator(string email, string password)
    {
        _email    = email;
        _password = password;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    public async Task<string> GetAccessTokenAsync()
    {
        var cache = LoadCache();

        if (cache is not null)
        {
            // 1. Cached token still valid
            if (cache.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                Console.WriteLine("[Auth] Using cached access token.");
                return cache.BoseAccessToken;
            }

            Console.WriteLine("[Auth] Cached token expired. Attempting token refresh...");

            // 2. Try Bose refresh token
            if (!string.IsNullOrEmpty(cache.BoseRefreshToken))
            {
                try
                {
                    var refreshed = await RefreshBoseTokenAsync(cache.BoseRefreshToken, cache.AzureRefreshToken);
                    SaveCache(refreshed);
                    Console.WriteLine("[Auth] Token refreshed via Bose refresh token.");
                    return refreshed.BoseAccessToken;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Auth] Bose refresh failed ({ex.Message}). Trying Azure refresh...");
                }
            }

            // 3. Try Azure refresh token
            if (!string.IsNullOrEmpty(cache.AzureRefreshToken))
            {
                try
                {
                    var refreshed = await RefreshViaAzureAsync(cache.AzureRefreshToken);
                    SaveCache(refreshed);
                    Console.WriteLine("[Auth] Token refreshed via Azure refresh token.");
                    return refreshed.BoseAccessToken;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Auth] Azure refresh failed ({ex.Message}). Falling back to full auth...");
                }
            }
        }

        // 4. Full PKCE authentication
        Console.WriteLine("[Auth] Starting full authentication flow...");
        var tokens = await FullAuthAsync();
        SaveCache(tokens);
        return tokens.BoseAccessToken;
    }

    // ── Full PKCE / OAuth2 auth flow ──────────────────────────────────────────

    private async Task<TokenData> FullAuthAsync()
    {
        var cookieContainer = new CookieContainer();
        using var http = new HttpClient(new HttpClientHandler
        {
            CookieContainer   = cookieContainer,
            AllowAutoRedirect = true
        });

        // PKCE
        var verifierBytes  = new byte[32];
        Random.Shared.NextBytes(verifierBytes);
        var codeVerifier   = ToBase64Url(verifierBytes);
        var codeChallenge  = ToBase64Url(System.Security.Cryptography.SHA256.HashData(
                                 Encoding.UTF8.GetBytes(codeVerifier)));

        // Step 1: Auth page → CSRF token + tx
        Console.WriteLine("[Auth/Step 1] Fetching auth page...");
        var authUrl = $"{BaseUrl}/{Tenant}/oauth2/v2.0/authorize" +
            $"?p={Policy}&response_type=code&client_id={ClientId}" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            $"&code_challenge_method=S256&code_challenge={codeChallenge}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}";

        var html = await http.GetStringAsync(authUrl);
        var csrfToken = ExtractCsrf(cookieContainer, html);
        var txParam   = ExtractTx(html);
        Console.WriteLine($"[Auth/Step 1] Done (CSRF: {(string.IsNullOrEmpty(csrfToken) ? "not found" : "ok")}, tx: {(string.IsNullOrEmpty(txParam) ? "not found" : "ok")})");

        // Step 2: Submit email
        Console.WriteLine("[Auth/Step 2] Submitting email...");
        var emailReq = BuildSelfAssertedRequest(csrfToken, txParam,
            new Dictionary<string, string> { ["request_type"] = "RESPONSE", ["email"] = _email });
        await http.SendAsync(emailReq);

        // Step 3: Confirm email page (refreshes CSRF + tx)
        Console.WriteLine("[Auth/Step 3] Fetching email confirmation page...");
        var confirmUrl = $"{BaseUrl}/{Tenant}/{Policy}/api/CombinedSigninAndSignup/confirmed" +
            $"?rememberMe=false&csrf_token={csrfToken}&tx={txParam}&p={Policy}";
        html      = await http.GetStringAsync(confirmUrl);
        csrfToken = ExtractCsrf(cookieContainer, html, fallback: csrfToken);
        txParam   = ExtractTx(html).IfEmpty(txParam);

        // Step 4: Submit password
        Console.WriteLine("[Auth/Step 4] Submitting password...");
        var passwordReq = BuildSelfAssertedRequest(csrfToken, txParam,
            new Dictionary<string, string> { ["readonlyEmail"] = _email, ["password"] = _password, ["request_type"] = "RESPONSE" });
        await http.SendAsync(passwordReq);

        // Step 5: Get auth code (follow redirect is disabled to capture the code)
        Console.WriteLine("[Auth/Step 5] Retrieving authorization code...");
        using var noRedirectHttp = new HttpClient(new HttpClientHandler
        {
            CookieContainer   = cookieContainer,
            AllowAutoRedirect = false
        });
        var confirm2Url  = $"{BaseUrl}/{Tenant}/{Policy}/api/SelfAsserted/confirmed?csrf_token={csrfToken}&tx={txParam}&p={Policy}";
        var confirm2Resp = await noRedirectHttp.GetAsync(confirm2Url);
        var location     = confirm2Resp.Headers.Location?.ToString() ?? "";
        var authCode     = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query)["code"]!;
        Console.WriteLine($"[Auth/Step 5] Authorization code obtained.");

        // Step 6: Exchange code for Azure tokens (includes refresh_token)
        Console.WriteLine("[Auth/Step 6] Exchanging auth code for Azure tokens...");
        var azureResp = await http.PostAsync(
            $"{BaseUrl}/{Tenant}/oauth2/v2.0/token?p={Policy}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = ClientId,
                ["code_verifier"] = codeVerifier,
                ["grant_type"]    = "authorization_code",
                ["scope"]         = Scope,
                ["redirect_uri"]  = RedirectUri,
                ["code"]          = authCode,
            }));
        var azureTokens       = JsonNode.Parse(await azureResp.Content.ReadAsStringAsync())!;
        var idToken           = azureTokens["id_token"]!.GetValue<string>();
        var azureRefreshToken = azureTokens["refresh_token"]?.GetValue<string>() ?? "";

        // Step 7: Exchange id_token for Bose access token
        Console.WriteLine("[Auth/Step 7] Exchanging id_token for Bose access token...");
        return await ExchangeIdTokenAsync(http, idToken, azureRefreshToken);
    }

    // ── Azure refresh flow ────────────────────────────────────────────────────

    private async Task<TokenData> RefreshViaAzureAsync(string azureRefreshToken)
    {
        using var http = new HttpClient();
        var resp = await http.PostAsync(
            $"{BaseUrl}/{Tenant}/oauth2/v2.0/token?p={Policy}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = ClientId,
                ["grant_type"]    = "refresh_token",
                ["refresh_token"] = azureRefreshToken,
                ["scope"]         = Scope,
            }));
        resp.EnsureSuccessStatusCode();
        var azureTokens = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        if (azureTokens["error"] is not null)
            throw new InvalidOperationException($"Azure refresh error: {azureTokens["error_description"]}");

        var idToken              = azureTokens["id_token"]!.GetValue<string>();
        var newAzureRefreshToken = azureTokens["refresh_token"]?.GetValue<string>() ?? azureRefreshToken;
        return await ExchangeIdTokenAsync(http, idToken, newAzureRefreshToken);
    }

    // ── Bose direct refresh flow ──────────────────────────────────────────────

    private static async Task<TokenData> RefreshBoseTokenAsync(string boseRefreshToken, string existingAzureRefreshToken)
    {
        using var http = new HttpClient();
        var req = BuildBoseRequest(JsonContent.Create(new
        {
            grant_type    = "refresh_token",
            refresh_token = boseRefreshToken,
            client_id     = ClientId,
        }));
        var respStr = await (await http.SendAsync(req)).Content.ReadAsStringAsync();
        var tokens  = JsonNode.Parse(respStr)!;
        if (tokens["error"] is not null || tokens["access_token"] is null)
            throw new InvalidOperationException($"Bose refresh error: {respStr}");

        // Preserve the Azure refresh token since Bose won't return a new one
        return BuildTokenData(tokens, azureRefreshToken: existingAzureRefreshToken);
    }

    // ── id_token → Bose access_token ─────────────────────────────────────────

    private static async Task<TokenData> ExchangeIdTokenAsync(HttpClient http, string idToken, string azureRefreshToken)
    {
        var req = BuildBoseRequest(JsonContent.Create(new
        {
            grant_type = "id_token",
            id_token   = idToken,
            client_id  = ClientId,
            scope      = Scope,
        }));
        var respStr = await (await http.SendAsync(req)).Content.ReadAsStringAsync();
        var tokens  = JsonNode.Parse(respStr)!;
        if (tokens["access_token"] is null)
            throw new InvalidOperationException($"Bose token exchange failed: {respStr}");

        return BuildTokenData(tokens, azureRefreshToken);
    }

    // ── Token cache helpers ───────────────────────────────────────────────────

    private static TokenData? LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFilePath)) return null;
            var json = File.ReadAllText(CacheFilePath);
            return JsonSerializer.Deserialize<TokenData>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(TokenData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);
        File.WriteAllText(CacheFilePath,
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[Auth] Token cached to: {CacheFilePath}");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private HttpRequestMessage BuildSelfAssertedRequest(string csrfToken, string txParam, Dictionary<string, string> fields)
    {
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{BaseUrl}/{Tenant}/{Policy}/SelfAsserted?tx={txParam}&p={Policy}");
        req.Headers.Add("X-CSRF-TOKEN", csrfToken);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Content = new FormUrlEncodedContent(fields);
        return req;
    }

    private static HttpRequestMessage BuildBoseRequest(HttpContent content)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, BoseTokenUrl);
        req.Headers.Add("X-ApiKey", BoseApiKey);
        req.Headers.Add("X-Api-Version", "1");
        req.Headers.Add("X-Software-Version", "1");
        req.Headers.Add("User-Agent", "Bose/37362 CFNetwork/3860.200.71 Darwin/25.1.0");
        req.Content = content;
        return req;
    }

    private static TokenData BuildTokenData(JsonNode tokens, string azureRefreshToken)
    {
        var accessToken      = tokens["access_token"]!.GetValue<string>();
        var boseRefreshToken = tokens["refresh_token"]?.GetValue<string>() ?? "";
        var expiresIn        = tokens["expires_in"]?.GetValue<int>() ?? 3600;
        Console.WriteLine($"[Auth] Bose access token obtained. Person ID: {tokens["bosePersonID"]}");
        return new TokenData(
            BoseAccessToken:   accessToken,
            BoseRefreshToken:  boseRefreshToken,
            AzureRefreshToken: azureRefreshToken,
            ExpiresAt:         DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private static string ExtractCsrf(CookieContainer cookies, string html, string? fallback = null)
    {
        var fromCookie = cookies.GetAllCookies().Cast<Cookie>()
            .FirstOrDefault(c => c.Name.Contains("csrf", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrEmpty(fromCookie)) return fromCookie;

        var m = System.Text.RegularExpressions.Regex.Match(
            html, @"x-ms-cpim-csrf[""'\s]*[=:][""'\s]*([^"";\s]+)");
        return m.Success ? m.Groups[1].Value : (fallback ?? "");
    }

    private static string ExtractTx(string html)
    {
        var m = System.Text.RegularExpressions.Regex.Match(html, @"[?&]tx=([^&""']+)");
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(html, @"StateProperties=([^&""']+)");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// ── Cached token data (serialised to disk) ────────────────────────────────────
internal record TokenData(
    string          BoseAccessToken,
    string          BoseRefreshToken,
    string          AzureRefreshToken,
    DateTimeOffset  ExpiresAt);

// ── String helper ─────────────────────────────────────────────────────────────
internal static class StringExtensions
{
    public static string IfEmpty(this string value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;
}
