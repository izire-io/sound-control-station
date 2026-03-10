using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

// ── 1. Authenticate via Azure AD B2C ──────────────────────────────────────────
// Mirrors BoseAuth.getControlToken() flow:
//   Azure AD B2C login → exchange id_token → Bose access_token

var cookieContainer = new CookieContainer();
using var httpClient = new HttpClient(new HttpClientHandler { CookieContainer = cookieContainer, AllowAutoRedirect = true });

const string boseSoundbarIp = "192.168.50.79"; // replace with your soundbar's IP address
const string email    = "";
const string password = "";
const string boseApiKey  = "67616C617061676F732D70726F642D6D61647269642D696F73"; // public key from BoseAuth.py
const string clientId    = "e284648d-3009-47eb-8e74-670c5330ae54";
const string policy      = "B2C_1A_MBI_SUSI";
const string tenant      = "boseprodb2c.onmicrosoft.com";
const string baseUrl     = "https://myboseid.bose.com";
const string redirectUri = "bosemusic://auth/callback";
const string scope       = $"openid email profile offline_access {clientId}";

Console.WriteLine("[Auth] Starting Azure AD B2C authentication flow...");

// --- PKCE ---
var verifierBytes  = new byte[32]; Random.Shared.NextBytes(verifierBytes);
var codeVerifier   = Convert.ToBase64String(verifierBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
var challengeBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
var codeChallenge  = Convert.ToBase64String(challengeBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

// --- Step 1: Get CSRF + tx from auth page ---
Console.WriteLine("[Step 1] Fetching auth page (CSRF token + transaction ID)...");
var authUrl = $"{baseUrl}/{tenant}/oauth2/v2.0/authorize" +
    $"?p={policy}&response_type=code&client_id={clientId}&scope={Uri.EscapeDataString(scope)}" +
    $"&code_challenge_method=S256&code_challenge={codeChallenge}&redirect_uri={Uri.EscapeDataString(redirectUri)}";

var html = await httpClient.GetStringAsync(authUrl);

// Extract CSRF token (from cookie, as BoseAuth._extract_csrf_token does)
var csrfToken = cookieContainer.GetAllCookies()
    .Cast<Cookie>()
    .FirstOrDefault(c => c.Name.Contains("csrf", StringComparison.OrdinalIgnoreCase))?.Value
    ?? System.Text.RegularExpressions.Regex.Match(html, @"x-ms-cpim-csrf[""'\s]*[=:][""'\s]*([^"";\s]+)").Groups[1].Value;

var txParam = System.Text.RegularExpressions.Regex.Match(html, @"[?&]tx=([^&""']+)").Groups[1].Value;
if (string.IsNullOrEmpty(txParam))
    txParam = System.Text.RegularExpressions.Regex.Match(html, @"StateProperties=([^&""']+)").Groups[1].Value;
Console.WriteLine($"[Step 1] Done. CSRF token: {(string.IsNullOrEmpty(csrfToken) ? "(not found)" : "obtained")}, tx: {(string.IsNullOrEmpty(txParam) ? "(not found)" : "obtained")}");

// --- Step 2: Submit email ---
Console.WriteLine($"[Step 2] Submitting email: {email}...");
var emailReq = new HttpRequestMessage(HttpMethod.Post,
    $"{baseUrl}/{tenant}/{policy}/SelfAsserted?tx={txParam}&p={policy}");
emailReq.Headers.Add("X-CSRF-TOKEN", csrfToken);
emailReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
emailReq.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
    ["request_type"] = "RESPONSE", ["email"] = email });
await httpClient.SendAsync(emailReq);
Console.WriteLine("[Step 2] Email submitted.");

// --- Step 3: Confirm email page ---
Console.WriteLine("[Step 3] Fetching email confirmation page...");
var confirmUrl = $"{baseUrl}/{tenant}/{policy}/api/CombinedSigninAndSignup/confirmed" +
    $"?rememberMe=false&csrf_token={csrfToken}&tx={txParam}&p={policy}";
html = await httpClient.GetStringAsync(confirmUrl);
csrfToken = cookieContainer.GetAllCookies().Cast<Cookie>()
    .FirstOrDefault(c => c.Name.Contains("csrf", StringComparison.OrdinalIgnoreCase))?.Value ?? csrfToken;
txParam = System.Text.RegularExpressions.Regex.Match(html, @"[?&]tx=([^&""']+)").Groups[1].Value.IfEmpty(txParam);
Console.WriteLine("[Step 3] Confirmation page fetched. Refreshed CSRF and tx.");

// --- Step 4: Submit password ---
Console.WriteLine("[Step 4] Submitting password...");
var passwordReq = new HttpRequestMessage(HttpMethod.Post,
    $"{baseUrl}/{tenant}/{policy}/SelfAsserted?tx={txParam}&p={policy}");
passwordReq.Headers.Add("X-CSRF-TOKEN", csrfToken);
passwordReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
passwordReq.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
    ["readonlyEmail"] = email, ["password"] = password, ["request_type"] = "RESPONSE" });
await httpClient.SendAsync(passwordReq);
Console.WriteLine("[Step 4] Password submitted.");

// --- Step 5: Get auth code (no redirect) ---
Console.WriteLine("[Step 5] Retrieving authorization code...");
using var noRedirectClient = new HttpClient(new HttpClientHandler { CookieContainer = cookieContainer, AllowAutoRedirect = false });
var confirm2Url = $"{baseUrl}/{tenant}/{policy}/api/SelfAsserted/confirmed?csrf_token={csrfToken}&tx={txParam}&p={policy}";
var confirm2Resp = await noRedirectClient.GetAsync(confirm2Url);
var location = confirm2Resp.Headers.Location?.ToString() ?? "";
var authCode = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query)["code"]!;
Console.WriteLine($"[Step 5] Authorization code obtained: {authCode[..Math.Min(8, authCode.Length)]}...");

// --- Step 6: Exchange code for Azure tokens ---
Console.WriteLine("[Step 6] Exchanging authorization code for Azure tokens...");
var tokenResp = await httpClient.PostAsync(
    $"{baseUrl}/{tenant}/oauth2/v2.0/token?p={policy}",
    new FormUrlEncodedContent(new Dictionary<string, string> {
        ["client_id"]     = clientId,
        ["code_verifier"] = codeVerifier,
        ["grant_type"]    = "authorization_code",
        ["scope"]         = scope,
        ["redirect_uri"]  = redirectUri,
        ["code"]          = authCode,
    }));
var azureTokens = JsonNode.Parse(await tokenResp.Content.ReadAsStringAsync())!;
var idToken = azureTokens["id_token"]!.GetValue<string>();
Console.WriteLine($"[Step 6] Azure tokens received. id_token length: {idToken.Length} chars.");

// --- Step 7: Exchange id_token for Bose access_token ---
// Mirrors BoseAuth._exchange_id_token_for_bose_tokens()
Console.WriteLine("[Step 7] Exchanging id_token for Bose access token...");
var boseTokenReq = new HttpRequestMessage(HttpMethod.Post,
    "https://id.api.bose.io/id-jwt-core/idps/aad/B2C_1A_MBI_SUSI/token");
boseTokenReq.Headers.Add("X-ApiKey", boseApiKey);
boseTokenReq.Headers.Add("X-Api-Version", "1");
boseTokenReq.Headers.Add("X-Software-Version", "1");
boseTokenReq.Headers.Add("User-Agent", "Bose/37362 CFNetwork/3860.200.71 Darwin/25.1.0");
boseTokenReq.Content = JsonContent.Create(new {
    grant_type = "id_token",
    id_token   = idToken,
    client_id  = clientId,
    scope      = scope,
});
var boseTokenResp = JsonNode.Parse(await (await httpClient.SendAsync(boseTokenReq)).Content.ReadAsStringAsync())!;
var accessToken = boseTokenResp["access_token"]!.GetValue<string>();

Console.WriteLine($"Bose access token obtained. Person ID: {boseTokenResp["bosePersonID"]}");

// ── 2. Discover your soundbar (Zeroconf/_bose-passport._tcp.local.) ───────────
// In C#, use Zeroconf NuGet package or mDNS library. Here we assume you already
// have the device IP and GUID (e.g. from the BoseDiscovery class output).
const string deviceIp = boseSoundbarIp;  // replace with discovered IP

// ── 3. Connect via WebSocket and GET /audio/volume ───────────────────────────
// Mirrors BoseSpeaker._request("/audio/volume", "GET")
// WebSocket URL from BoseSpeaker constructor:
//   wss://<host>:8082/?product=Madrid-iOS:31019F02-F01F-4E73-B495-B96D33AD3664
// Subprotocol: "eco2"  (from BoseSpeaker._subprotocol)
// TLS: skip certificate verification (device uses self-signed cert)

Console.WriteLine($"[WebSocket] Connecting to device at {deviceIp}...");
var wsUrl = new Uri($"wss://{deviceIp}:8082/?product=Madrid-iOS:31019F02-F01F-4E73-B495-B96D33AD3664");
using var ws = new ClientWebSocket();
ws.Options.AddSubProtocol("eco2");
// Skip cert validation (mirrors ssl_context.verify_mode = CERT_NONE in BoseSpeaker)
ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

await ws.ConnectAsync(wsUrl, CancellationToken.None);
Console.WriteLine("[WebSocket] Connected.");

// Step 3a: Read the first message to get the device ID
//          (BoseSpeaker sets _device_id from the first incoming message header)
Console.WriteLine("[Step 3a] Reading first message to obtain device ID...");
var buf = new byte[65536];
var result = await ws.ReceiveAsync(buf, CancellationToken.None);
var firstMsg = JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, result.Count))!;
var deviceId = firstMsg["header"]?["device"]?.GetValue<string>() ?? "";
Console.WriteLine($"[Step 3a] Device ID: {(string.IsNullOrEmpty(deviceId) ? "(not found)" : deviceId)}");

// Step 3b: GET /system/capabilities (BoseSpeaker.connect() calls get_capabilities first)
Console.WriteLine("[Step 3b] Requesting /system/capabilities...");
var capReqId = 1;
var capRequest = new {
    header = new {
        device   = deviceId,
        method   = "GET",
        msgtype  = "REQUEST",
        reqID    = capReqId,
        resource = "/system/capabilities",
        status   = 200,
        token    = accessToken,
        version  = 1
    },
    body = new { }
};
var capJson = JsonSerializer.Serialize(capRequest);
await ws.SendAsync(Encoding.UTF8.GetBytes(capJson), WebSocketMessageType.Text, true, CancellationToken.None);

// Read capabilities response (skip until we get the matching reqID)
JsonNode? capResponse = null;
while (capResponse == null) {
    result = await ws.ReceiveAsync(buf, CancellationToken.None);
    var msg = JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, result.Count))!;
    if (msg["header"]?["reqID"]?.GetValue<int>() == capReqId &&
        msg["header"]?["msgtype"]?.GetValue<string>() == "RESPONSE")
        capResponse = msg;
}
Console.WriteLine("[Step 3b] Capabilities response received.");

// Step 3c: GET /audio/volume — mirrors BoseSpeaker.get_audio_volume()
Console.WriteLine("[Step 3c] Requesting /audio/volume...");
var volReqId = 2;
var volRequest = new {
    header = new {
        device   = deviceId,
        method   = "GET",
        msgtype  = "REQUEST",
        reqID    = volReqId,
        resource = "/audio/volume",
        status   = 200,
        token    = accessToken,
        version  = 1
    },
    body = new { }
};
await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(volRequest)),
    WebSocketMessageType.Text, true, CancellationToken.None);

JsonNode? volumeResponse = null;
while (volumeResponse == null) {
    result = await ws.ReceiveAsync(buf, CancellationToken.None);
    var msg = JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, result.Count))!;
    if (msg["header"]?["reqID"]?.GetValue<int>() == volReqId &&
        msg["header"]?["msgtype"]?.GetValue<string>() == "RESPONSE")
        volumeResponse = msg;
}

var body = volumeResponse["body"]!;
Console.WriteLine($"Volume: {body["value"]}  (min:{body["min"]}, max:{body["max"]}, muted:{body["muted"]})");

Console.WriteLine("[WebSocket] Closing connection.");
await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
Console.WriteLine("[Done] Flow completed successfully.");

static class StringExtensions
{
    public static string IfEmpty(this string value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;
}