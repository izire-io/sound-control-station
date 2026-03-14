using System;
using System.Text.Json;
using System.Threading.Tasks;

// ── Configuration ─────────────────────────────────────────────────────────────
const string boseSoundbarIp = "192.168.50.79"; // replace with your soundbar's IP
const string email    = "";
const string password = "";

// ── 1. Authenticate ───────────────────────────────────────────────────────────
var auth        = new BoseAuthenticator(email, password);
var accessToken = await auth.GetAccessTokenAsync();

// ── 2. Connect ────────────────────────────────────────────────────────────────
await using var speaker = new BoseSpeakerClient(boseSoundbarIp, accessToken);
await speaker.ConnectAsync();

// ── 3. System info ────────────────────────────────────────────────────────────
var info = await speaker.GetSystemInfoAsync();
Console.WriteLine($"\nDevice : {info["name"]} ({info["type"]})");
Console.WriteLine($"Serial : {info["serialNumber"]}");

var power = await speaker.GetPowerStateAsync();
Console.WriteLine($"Power  : {power}");

// ── 4. Volume ─────────────────────────────────────────────────────────────────
var volume = await speaker.GetVolumeAsync();
Console.WriteLine($"\nVolume : {volume}");

// Raise volume by 5, then restore original
await speaker.SetVolumeAsync(volume.Value + 5);
await Task.Delay(1500);
await speaker.SetVolumeAsync(volume.Value);

// Toggle mute on/off
await speaker.SetMuteAsync(true);
await Task.Delay(1000);
await speaker.SetMuteAsync(false);

// ── 5. Equaliser ─────────────────────────────────────────────────────────────
var bass   = await speaker.GetBassAsync();
var treble = await speaker.GetTrebleAsync();
Console.WriteLine($"\nBass   : {bass}");
Console.WriteLine($"Treble : {treble}");

// Bump bass by 1 step, then restore
await speaker.SetBassAsync(bass.Value + 1);
await Task.Delay(1000);
await speaker.SetBassAsync(bass.Value);

// ── 6. Audio mode ─────────────────────────────────────────────────────────────
var mode = await speaker.GetAudioModeAsync();
Console.WriteLine($"\nAudio mode : {mode}");

// ── 7. Surround / subwoofer ───────────────────────────────────────────────────
var subEnabled = await speaker.GetSubwooferAsync();
var subGain    = await speaker.GetSubwooferGainAsync();
Console.WriteLine($"\nSubwoofer  : {(subEnabled ? "on" : "off")}, gain {subGain}");

var avSync = await speaker.GetAvSyncAsync();
Console.WriteLine($"AV Sync    : {avSync} ms");

// ── 8. Now playing ────────────────────────────────────────────────────────────
var nowPlaying = await speaker.GetNowPlayingAsync();
Console.WriteLine($"\nNow playing: {nowPlaying["track"]} — {nowPlaying["artist"]}");
Console.WriteLine($"Source     : {nowPlaying["source"]}");

// ── 9. Playback controls ──────────────────────────────────────────────────────
// Pause, wait, resume
await speaker.SetTransportControlAsync(TransportControlAction.PAUSE);
await Task.Delay(2000);
await speaker.SetTransportControlAsync(TransportControlAction.PLAY);

// Skip to next track
await speaker.SkipNextAsync();

// Set shuffle + repeat
await speaker.SetShuffleAsync(true);
await speaker.SetRepeatAsync(RepeatMode.ALL);

// Rate current track
await speaker.RateNowPlayingAsync(TrackRating.UP);

// ── 10. Sources ───────────────────────────────────────────────────────────────
var sources = await speaker.GetAudioSourcesAsync();
Console.WriteLine($"\nSources    : {sources.ToJsonString(new JsonSerializerOptions { WriteIndented = false })}");

// ── 11. Presets ───────────────────────────────────────────────────────────────
var presets = await speaker.GetPresetsAsync();
Console.WriteLine($"\nPresets    : {presets.ToJsonString(new JsonSerializerOptions { WriteIndented = false })}");

// Recall preset 1 (if stored)
await speaker.RecallPresetAsync(1);

// ── 12. Accessories & Wi-Fi ───────────────────────────────────────────────────
var accessories = await speaker.GetAccessoriesAsync();
Console.WriteLine($"\nAccessories: {accessories.ToJsonString(new JsonSerializerOptions { WriteIndented = false })}");

var wifi = await speaker.GetWifiStatusAsync();
Console.WriteLine($"Wi-Fi      : {wifi["ssid"]} ({wifi["state"]})");

Console.WriteLine("\n[Done] All examples completed successfully.");