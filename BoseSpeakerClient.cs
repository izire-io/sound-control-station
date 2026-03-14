using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Controls a Bose soundbar over its local WebSocket (eco2) API.
/// Mirrors the full pybose BoseSpeaker method surface.
/// Usage:
///   await using var speaker = new BoseSpeakerClient("192.168.x.x", accessToken);
///   await speaker.ConnectAsync();
///   var vol = await speaker.GetVolumeAsync();
///   await speaker.SetVolumeAsync(30);
/// </summary>
public sealed class BoseSpeakerClient : IAsyncDisposable
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const int    WsPort        = 8082;
    private const string Subprotocol   = "eco2";
    private const string ProductString = "Madrid-iOS:31019F02-F01F-4E73-B495-B96D33AD3664";
    private const int    RecvBufSize   = 65536;

    // ── Resources ─────────────────────────────────────────────────────────────
    private const string ResVolume               = "/audio/volume";
    private const string ResBass                 = "/audio/bass";
    private const string ResTreble               = "/audio/treble";
    private const string ResMode                 = "/audio/mode";
    private const string ResSources              = "/audio/sources";
    private const string ResCenterSpeaker        = "/audio/centerSpeaker";
    private const string ResSurround             = "/audio/surround";
    private const string ResSurroundLevel        = "/audio/surroundSpeakerLevel";
    private const string ResRearSpeakers         = "/audio/rearSpeakers";
    private const string ResRearSpeakerLevel     = "/audio/rearSpeakerLevel";
    private const string ResSubwoofer            = "/audio/subwoofer";
    private const string ResSubwooferGain        = "/audio/subwoofer/gain";
    private const string ResAvSync               = "/audio/avSync";
    private const string ResCapabilities         = "/system/capabilities";
    private const string ResSystemInfo           = "/system/info";
    private const string ResPower                = "/system/power";
    private const string ResNowPlaying           = "/content/nowPlaying";
    private const string ResRating               = "/content/nowPlaying/rating";
    private const string ResRepeat               = "/content/nowPlaying/repeat";
    private const string ResShuffle              = "/content/nowPlaying/shuffle";
    private const string ResSkipNext             = "/content/nowPlaying/skip_next";
    private const string ResSkipPrev             = "/content/nowPlaying/skip_prev";
    private const string ResTransportControl     = "/content/transportControl";
    private const string ResContentItem         = "/content/contentItem";
    private const string ResPresets              = "/ui/presets";
    private const string ResNetworkWifiStatus    = "/network/wifi/status";
    private const string ResAccessories         = "/accessories";

    private readonly string _host;
    private readonly string _accessToken;
    private readonly ClientWebSocket _ws;
    private string _deviceId = "";
    private int _reqIdCounter;

    public BoseSpeakerClient(string host, string accessToken)
    {
        _host        = host;
        _accessToken = accessToken;
        _ws          = new ClientWebSocket();
        _ws.Options.AddSubProtocol(Subprotocol);
        // The device uses a self-signed TLS certificate.
        _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
    }

    // ── Connection lifecycle ──────────────────────────────────────────────────

    /// <summary>
    /// Opens the WebSocket connection, reads the device ID from the first
    /// server-sent message, and fetches /system/capabilities (required by the
    /// Bose protocol before issuing other commands).
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var uri = new Uri($"wss://{_host}:{WsPort}/?product={ProductString}");
        Console.WriteLine($"[Speaker] Connecting to {uri}...");
        await _ws.ConnectAsync(uri, ct);
        Console.WriteLine("[Speaker] Connected.");

        // First message from the server carries the device ID.
        var firstMsg = await ReceiveMessageAsync(ct);
        _deviceId = firstMsg["header"]?["device"]?.GetValue<string>() ?? "";
        Console.WriteLine($"[Speaker] Device ID: {(_deviceId.Length > 0 ? _deviceId : "(not found)")}");

        // Capabilities must be fetched before any other command.
        Console.WriteLine("[Speaker] Fetching capabilities...");
        var caps = await RequestAsync(ResCapabilities, "GET", ct: ct);
        Console.WriteLine("[Speaker] Ready.");
    }

    /// <summary>Closes the WebSocket connection gracefully.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_ws.State == WebSocketState.Open)
        {
            Console.WriteLine("[Speaker] Disconnecting...");
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
            Console.WriteLine("[Speaker] Disconnected.");
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    // ── Volume ────────────────────────────────────────────────────────────────

    /// <summary>Returns the current volume state of the device.</summary>
    public async Task<VolumeInfo> GetVolumeAsync(CancellationToken ct = default)
    {
        var body = (await RequestAsync(ResVolume, "GET", ct: ct))["body"];
        return new VolumeInfo(
            Value:   body?["value"]?.GetValue<int>()   ?? 0,
            Min:     body?["min"]?.GetValue<int>()     ?? 0,
            Max:     body?["max"]?.GetValue<int>()     ?? 100,
            IsMuted: body?["muted"]?.GetValue<bool>()  ?? false);
    }

    /// <summary>Sets the volume to an absolute value.</summary>
    public async Task SetVolumeAsync(int value, CancellationToken ct = default)
    {
        await RequestAsync(ResVolume, "PUT", new { value }, ct);
        Console.WriteLine($"[Speaker] Volume set to {value}.");
    }

    /// <summary>Mutes or unmutes the device.</summary>
    public async Task SetMuteAsync(bool muted, CancellationToken ct = default)
    {
        await RequestAsync(ResVolume, "PUT", new { muted }, ct);
        Console.WriteLine($"[Speaker] Muted: {muted}.");
    }

    // ── Bass / Treble ─────────────────────────────────────────────────────────

    /// <summary>Returns the current bass level and its supported range.</summary>
    public async Task<AudioLevel> GetBassAsync(CancellationToken ct = default) =>
        ParseAudioLevel((await RequestAsync(ResBass, "GET", ct: ct))["body"]);

    /// <summary>Sets the bass level.</summary>
    public async Task SetBassAsync(int level, CancellationToken ct = default)
    {
        await RequestAsync(ResBass, "PUT", new { value = level }, ct);
        Console.WriteLine($"[Speaker] Bass set to {level}.");
    }

    /// <summary>Returns the current treble level and its supported range.</summary>
    public async Task<AudioLevel> GetTrebleAsync(CancellationToken ct = default) =>
        ParseAudioLevel((await RequestAsync(ResTreble, "GET", ct: ct))["body"]);

    /// <summary>Sets the treble level.</summary>
    public async Task SetTrebleAsync(int level, CancellationToken ct = default)
    {
        await RequestAsync(ResTreble, "PUT", new { value = level }, ct);
        Console.WriteLine($"[Speaker] Treble set to {level}.");
    }

    // ── Audio mode ────────────────────────────────────────────────────────────

    /// <summary>Returns the current audio mode (e.g. "STEREO", "SURROUND").</summary>
    public async Task<string> GetAudioModeAsync(CancellationToken ct = default)
    {
        var body = (await RequestAsync(ResMode, "GET", ct: ct))["body"];
        return body?["value"]?.GetValue<string>() ?? "";
    }

    /// <summary>Sets the audio mode.</summary>
    public async Task SetAudioModeAsync(string mode, CancellationToken ct = default)
    {
        await RequestAsync(ResMode, "PUT", new { value = mode }, ct);
        Console.WriteLine($"[Speaker] Audio mode set to {mode}.");
    }

    // ── Surround / center / rear speaker settings ────────────────────────────

    /// <summary>Gets the center speaker level.</summary>
    public async Task<AudioLevel> GetCenterSpeakerAsync(CancellationToken ct = default) =>
        ParseAudioLevel((await RequestAsync(ResCenterSpeaker, "GET", ct: ct))["body"]);

    /// <summary>Sets the center speaker level.</summary>
    public async Task SetCenterSpeakerAsync(int level, CancellationToken ct = default)
    {
        await RequestAsync(ResCenterSpeaker, "PUT", new { value = level }, ct);
        Console.WriteLine($"[Speaker] Center speaker level set to {level}.");
    }

    /// <summary>Returns whether surround speakers are enabled.</summary>
    public async Task<bool> GetSurroundAsync(CancellationToken ct = default)
    {
        var body = (await RequestAsync(ResSurround, "GET", ct: ct))["body"];
        return body?["enabled"]?.GetValue<bool>() ?? false;
    }

    /// <summary>Enables or disables surround speakers.</summary>
    public async Task SetSurroundAsync(bool enabled, CancellationToken ct = default)
    {
        await RequestAsync(ResSurround, "PUT", new { enabled }, ct);
        Console.WriteLine($"[Speaker] Surround: {enabled}.");
    }

    /// <summary>Gets the surround speaker level.</summary>
    public async Task<AudioLevel> GetSurroundLevelAsync(CancellationToken ct = default) =>
        ParseAudioLevel((await RequestAsync(ResSurroundLevel, "GET", ct: ct))["body"]);

    /// <summary>Sets the surround speaker level.</summary>
    public async Task SetSurroundLevelAsync(int level, CancellationToken ct = default)
    {
        await RequestAsync(ResSurroundLevel, "PUT", new { value = level }, ct);
        Console.WriteLine($"[Speaker] Surround level set to {level}.");
    }

    /// <summary>Returns whether rear speakers are enabled.</summary>
    public async Task<bool> GetRearSpeakersAsync(CancellationToken ct = default)
    {
        var body = (await RequestAsync(ResRearSpeakers, "GET", ct: ct))["body"];
        return body?["enabled"]?.GetValue<bool>() ?? false;
    }

    /// <summary>Enables or disables rear speakers.</summary>
    public async Task SetRearSpeakersAsync(bool enabled, CancellationToken ct = default)
    {
        await RequestAsync(ResRearSpeakers, "PUT", new { enabled }, ct);
        Console.WriteLine($"[Speaker] Rear speakers: {enabled}.");
    }

    /// <summary>Gets the rear speaker level.</summary>
    public async Task<AudioLevel> GetRearSpeakerLevelAsync(CancellationToken ct = default) =>
        ParseAudioLevel((await RequestAsync(ResRearSpeakerLevel, "GET", ct: ct))["body"]);

    /// <summary>Sets the rear speaker level.</summary>
    public async Task SetRearSpeakerLevelAsync(int level, CancellationToken ct = default)
    {
        await RequestAsync(ResRearSpeakerLevel, "PUT", new { value = level }, ct);
        Console.WriteLine($"[Speaker] Rear speaker level set to {level}.");
    }

    // ── Subwoofer ─────────────────────────────────────────────────────────────

    /// <summary>Returns whether the subwoofer is enabled.</summary>
    public async Task<bool> GetSubwooferAsync(CancellationToken ct = default)
    {
        var body = (await RequestAsync(ResSubwoofer, "GET", ct: ct))["body"];
        return body?["enabled"]?.GetValue<bool>() ?? false;
    }

    /// <summary>Enables or disables the subwoofer.</summary>
    public async Task SetSubwooferAsync(bool enabled, CancellationToken ct = default)
    {
        await RequestAsync(ResSubwoofer, "PUT", new { enabled }, ct);
        Console.WriteLine($"[Speaker] Subwoofer: {enabled}.");
    }

    /// <summary>Gets the subwoofer gain level.</summary>
    public async Task<AudioLevel> GetSubwooferGainAsync(CancellationToken ct = default) =>
        ParseAudioLevel((await RequestAsync(ResSubwooferGain, "GET", ct: ct))["body"]);

    /// <summary>Sets the subwoofer gain level.</summary>
    public async Task SetSubwooferGainAsync(int level, CancellationToken ct = default)
    {
        await RequestAsync(ResSubwooferGain, "PUT", new { value = level }, ct);
        Console.WriteLine($"[Speaker] Subwoofer gain set to {level}.");
    }

    // ── AV Sync ───────────────────────────────────────────────────────────────

    /// <summary>Gets the AV sync delay in milliseconds.</summary>
    public async Task<AudioLevel> GetAvSyncAsync(CancellationToken ct = default) =>
        ParseAudioLevel((await RequestAsync(ResAvSync, "GET", ct: ct))["body"]);

    /// <summary>Sets the AV sync delay in milliseconds.</summary>
    public async Task SetAvSyncAsync(int delayMs, CancellationToken ct = default)
    {
        await RequestAsync(ResAvSync, "PUT", new { value = delayMs }, ct);
        Console.WriteLine($"[Speaker] AV sync delay set to {delayMs} ms.");
    }

    // ── Power ─────────────────────────────────────────────────────────────────

    /// <summary>Returns the current power state ("ON" / "STANDBY" / …).</summary>
    public async Task<string> GetPowerStateAsync(CancellationToken ct = default)
    {
        var resp = await RequestAsync(ResPower, "GET", ct: ct);
        return resp["body"]?["value"]?.GetValue<string>() ?? "";
    }

    /// <summary>Sends a power on/standby command.</summary>
    public async Task SetPowerAsync(bool on, CancellationToken ct = default)
    {
        var state = on ? "ON" : "STANDBY";
        await RequestAsync(ResPower, "PUT", new { value = state }, ct);
        Console.WriteLine($"[Speaker] Power set to {state}.");
    }

    // ── System info ───────────────────────────────────────────────────────────

    /// <summary>Returns device information (name, product type, serial number, etc.).</summary>
    public async Task<JsonNode> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var resp = await RequestAsync(ResSystemInfo, "GET", ct: ct);
        return resp["body"]!;
    }

    // ── Sources ───────────────────────────────────────────────────────────────

    /// <summary>Returns the list of available audio sources.</summary>
    public async Task<JsonNode> GetAudioSourcesAsync(CancellationToken ct = default)
    {
        var resp = await RequestAsync(ResSources, "GET", ct: ct);
        return resp["body"]!;
    }

    /// <summary>Selects an audio source by name (e.g. "PRODUCT", "BLUETOOTH", "TV").</summary>
    public async Task SetAudioSourceAsync(string sourceName, CancellationToken ct = default)
    {
        await RequestAsync(ResSources, "PUT", new { value = sourceName }, ct);
        Console.WriteLine($"[Speaker] Audio source set to {sourceName}.");
    }

    // ── Now Playing / Transport ───────────────────────────────────────────────

    /// <summary>Returns the currently playing content info.</summary>
    public async Task<JsonNode> GetNowPlayingAsync(CancellationToken ct = default)
    {
        var resp = await RequestAsync(ResNowPlaying, "GET", ct: ct);
        return resp["body"]!;
    }

    /// <summary>Sends a transport control command: PLAY, PAUSE, STOP.</summary>
    public async Task SetTransportControlAsync(TransportControlAction action, CancellationToken ct = default)
    {
        var value = action.ToString();
        await RequestAsync(ResTransportControl, "PUT", new { value }, ct);
        Console.WriteLine($"[Speaker] Transport: {value}.");
    }

    /// <summary>Skip to the next track.</summary>
    public async Task SkipNextAsync(CancellationToken ct = default)
    {
        await RequestAsync(ResSkipNext, "POST", ct: ct);
        Console.WriteLine("[Speaker] Skipped to next track.");
    }

    /// <summary>Skip to the previous track.</summary>
    public async Task SkipPreviousAsync(CancellationToken ct = default)
    {
        await RequestAsync(ResSkipPrev, "POST", ct: ct);
        Console.WriteLine("[Speaker] Skipped to previous track.");
    }

    /// <summary>Sets the repeat mode: OFF, ONE, ALL.</summary>
    public async Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default)
    {
        var value = mode.ToString();
        await RequestAsync(ResRepeat, "PUT", new { value }, ct);
        Console.WriteLine($"[Speaker] Repeat mode: {value}.");
    }

    /// <summary>Enables or disables shuffle.</summary>
    public async Task SetShuffleAsync(bool enabled, CancellationToken ct = default)
    {
        await RequestAsync(ResShuffle, "PUT", new { enabled }, ct);
        Console.WriteLine($"[Speaker] Shuffle: {enabled}.");
    }

    /// <summary>Rates the currently playing track: UP, DOWN, or NONE.</summary>
    public async Task RateNowPlayingAsync(TrackRating rating, CancellationToken ct = default)
    {
        var value = rating.ToString();
        await RequestAsync(ResRating, "PUT", new { value }, ct);
        Console.WriteLine($"[Speaker] Rating: {value}.");
    }

    // ── Content item ──────────────────────────────────────────────────────────

    /// <summary>Selects a content item (e.g. a preset's stored content object).</summary>
    public async Task SelectContentItemAsync(object contentItem, CancellationToken ct = default)
    {
        await RequestAsync(ResContentItem, "PUT", contentItem, ct);
        Console.WriteLine("[Speaker] Content item selected.");
    }

    // ── Presets ───────────────────────────────────────────────────────────────

    /// <summary>Returns all stored presets (1–6).</summary>
    public async Task<JsonNode> GetPresetsAsync(CancellationToken ct = default)
    {
        var resp = await RequestAsync(ResPresets, "GET", ct: ct);
        return resp["body"]!;
    }

    /// <summary>Recalls (plays) a stored preset by its 1-based index.</summary>
    public async Task RecallPresetAsync(int presetIndex, CancellationToken ct = default)
    {
        await RequestAsync($"{ResPresets}/{presetIndex}", "GET", ct: ct);
        Console.WriteLine($"[Speaker] Preset {presetIndex} recalled.");
    }

    /// <summary>Stores the current content as a named preset.</summary>
    public async Task SetPresetAsync(int presetIndex, object presetContent, CancellationToken ct = default)
    {
        await RequestAsync($"{ResPresets}/{presetIndex}", "PUT", presetContent, ct);
        Console.WriteLine($"[Speaker] Preset {presetIndex} saved.");
    }

    // ── Capabilities ─────────────────────────────────────────────────────────

    /// <summary>Returns the full capabilities document for the device.</summary>
    public async Task<JsonNode> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        var resp = await RequestAsync(ResCapabilities, "GET", ct: ct);
        return resp["body"]!;
    }

    // ── Network ───────────────────────────────────────────────────────────────

    /// <summary>Returns the Wi-Fi connection status of the device.</summary>
    public async Task<JsonNode> GetWifiStatusAsync(CancellationToken ct = default)
    {
        var resp = await RequestAsync(ResNetworkWifiStatus, "GET", ct: ct);
        return resp["body"]!;
    }

    // ── Accessories ───────────────────────────────────────────────────────────

    /// <summary>Returns attached/paired accessories (e.g. bass module, surround speakers).</summary>
    public async Task<JsonNode> GetAccessoriesAsync(CancellationToken ct = default)
    {
        var resp = await RequestAsync(ResAccessories, "GET", ct: ct);
        return resp["body"]!;
    }

    // ── Generic request ───────────────────────────────────────────────────────

    /// <summary>
    /// Sends a typed request to the device and waits for the matching response.
    /// </summary>
    /// <param name="resource">API resource path, e.g. "/audio/volume".</param>
    /// <param name="method">HTTP-style verb: "GET", "PUT", "POST", "DELETE".</param>
    /// <param name="body">Optional request body (will be serialised to JSON).</param>
    public async Task<JsonNode> RequestAsync(
        string resource, string method, object? body = null,
        CancellationToken ct = default)
    {
        var reqId = Interlocked.Increment(ref _reqIdCounter);
        var payload = JsonSerializer.Serialize(new
        {
            header = new
            {
                device   = _deviceId,
                method   = method,
                msgtype  = "REQUEST",
                reqID    = reqId,
                resource = resource,
                status   = 200,
                token    = _accessToken,
                version  = 1
            },
            body = body ?? new { }
        });
        await _ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);

        // Drain incoming messages until we see our response.
        while (true)
        {
            var msg = await ReceiveMessageAsync(ct);
            if (msg["header"]?["reqID"]?.GetValue<int>() == reqId &&
                msg["header"]?["msgtype"]?.GetValue<string>() == "RESPONSE")
                return msg;
        }
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Safely parses a value/min/max AudioLevel from a possibly-null body node.
    /// Falls back to zero when a field is absent (device doesn't support it).
    /// </summary>
    private static AudioLevel ParseAudioLevel(JsonNode? body) =>
        new(body?["value"]?.GetValue<int>() ?? 0,
            body?["min"]?.GetValue<int>()   ?? 0,
            body?["max"]?.GetValue<int>()   ?? 0);

    private async Task<JsonNode> ReceiveMessageAsync(CancellationToken ct)
    {
        var buf    = new byte[RecvBufSize];
        var result = await _ws.ReceiveAsync(buf, ct);
        return JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, result.Count))!;
    }
}

// ── Value types ───────────────────────────────────────────────────────────────

/// <summary>Represents the current volume state returned by the device.</summary>
public record VolumeInfo(int Value, int Min, int Max, bool IsMuted)
{
    public override string ToString() =>
        $"{Value} (range {Min}-{Max}{(IsMuted ? ", muted" : "")})";
}

/// <summary>A generic numeric level with its supported range (bass, treble, surround, etc.).</summary>
public record AudioLevel(int Value, int Min, int Max)
{
    public override string ToString() => $"{Value} (range {Min}-{Max})";
}

/// <summary>Transport control actions.</summary>
public enum TransportControlAction { PLAY, PAUSE, STOP }

/// <summary>Repeat modes.</summary>
public enum RepeatMode { OFF, ONE, ALL }

/// <summary>Track rating values.</summary>
public enum TrackRating { UP, DOWN, NONE }
