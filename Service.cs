using System.Drawing;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace SkyboxChanger;

public class Service
{
  private readonly Storage _storage;
  private readonly SkyboxChanger _plugin;

  public Service(SkyboxChanger plugin, string host, int port, string user, string password, string database, string tablePrefix)
  {
    _plugin = plugin;
    _storage = new Storage(host, port, user, password, database, tablePrefix);
  }

  // ── Storage key ─────────────────────────────────────────────────────────────

  /// <summary>
  /// The id a player's row is cached and saved under.
  ///
  /// <para>AuthorizedSteamID first, because that is what the connect-time load and the disconnect
  /// save use - reading the cache under player.SteamID instead meant that whenever the two differ
  /// (SteamID is 0 until the controller is populated) a freshly loaded row was invisible and the
  /// getter minted a default on top of it, which then read as "it didn't save".</para>
  /// </summary>
  private static ulong StorageKey(CCSPlayerController player)
    => player.AuthorizedSteamID?.SteamId64 ?? player.SteamID;

  // ── Skybox ──────────────────────────────────────────────────────────────────

  public bool SetSkybox(CCSPlayerController player, string index)
  {
    if (_plugin.SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
    {
      return false;
    }

    // Validate BEFORE touching the stored key. Assigning first meant a key that is no longer in
    // config - the chat menu captures its keys when it is built, and "" (the map default) is
    // removed from Config.Skyboxs on every map end - wrote that dead key into the cached row and
    // then returned false without saving. The next Save, on disconnect or map end, wrote it over
    // the player's real choice.
    if (!_plugin.Config.Skyboxs.TryGetValue(index, out var skybox))
    {
      _plugin.Logger.LogError("[SkyboxChanger] SetSkybox failed: skybox key '{Index}' not found in Config.Skyboxs (available keys: {Keys})", index, string.Join(", ", _plugin.Config.Skyboxs.Keys));
      return false;
    }

    var skyData = _storage.GetPlayerSkydata(StorageKey(player));
    skyData.Skybox = index;

    if (skybox.Brightness != null)
    {
      _plugin.EnvManager.SetBrightness(player.Slot, skybox.Brightness.Value);
      skyData.Brightness = skybox.Brightness.Value;
    }

    if (skybox.Color != null)
    {
      var parts = skybox.Color.Split(' ');
      if (parts.Length == 4)
      {
        var r = int.Parse(parts[0]);
        var g = int.Parse(parts[1]);
        var b = int.Parse(parts[2]);
        var a = int.Parse(parts[3]);
        var color = Color.FromArgb(a, r, g, b);
        _plugin.EnvManager.SetTintColor(player.Slot, color);
        skyData.Color = color.ToArgb();
      }
    }

    _ = _storage.SaveAsync(StorageKey(player));
    var result = _plugin.EnvManager.SetSkybox(player.Slot, skybox);
    if (!result)
    {
      _plugin.Logger.LogError("[SkyboxChanger] Failed to apply skybox '{Index}' (material '{Material}') for slot={Slot}", index, skybox.Material, player.Slot);
    }
    return result;
  }

  public void SetBrightness(CCSPlayerController player, float brightness)
  {
    if (_plugin.SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
      return;

    var skyData = _storage.GetPlayerSkydata(StorageKey(player));
    skyData.Brightness = brightness;
    _plugin.EnvManager.SetBrightness(player.Slot, brightness);
    _ = _storage.SaveAsync(StorageKey(player));
  }

  public void SetTintColor(CCSPlayerController player, Color color)
  {
    if (_plugin.SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
      return;

    var skyData = _storage.GetPlayerSkydata(StorageKey(player));
    skyData.Color = color.ToArgb();
    _plugin.EnvManager.SetTintColor(player.Slot, color);
    _ = _storage.SaveAsync(StorageKey(player));
  }

  // ── Getters ─────────────────────────────────────────────────────────────────

  public Skybox? GetPlayerSkybox(CCSPlayerController player)
  {
    var data = _storage.GetPlayerSkydata(StorageKey(player));
    return _plugin.Config.Skyboxs.GetValueOrDefault(data.Skybox);
  }

  /// <summary>The stored key rather than the resolved <see cref="Skybox"/>, which is what the
  /// panel needs to mark the selected cell - two config entries can share a material, so the
  /// object does not identify which one the player picked.</summary>
  public string GetPlayerSkyboxKey(CCSPlayerController player)
  {
    return _storage.GetPlayerSkydata(StorageKey(player)).Skybox;
  }

  public float GetPlayerBrightness(CCSPlayerController player)
  {
    return _storage.GetPlayerSkydata(StorageKey(player)).Brightness;
  }

  public Color GetPlayerColor(CCSPlayerController player)
  {
    return Color.FromArgb(_storage.GetPlayerSkydata(StorageKey(player)).Color);
  }

  public Skybox? GetMapDefaultSkybox(string map)
  {
    var maps = _plugin.Config.MapDefault;
    if (maps == null) return null;
    if (maps.TryGetValue(map, out var key)) return _plugin.Config.Skyboxs.GetValueOrDefault(key);
    if (maps.TryGetValue("*", out var wildcard)) return _plugin.Config.Skyboxs.GetValueOrDefault(wildcard);
    return null;
  }

  // ── Apply stored settings to a player ───────────────────────────────────────

  public void ApplyPlayerSettings(CCSPlayerController player)
  {
    if (_plugin.SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
      return;

    var skyData = _storage.GetPlayerSkydata(StorageKey(player));

    if (!string.IsNullOrEmpty(skyData.Skybox) && _plugin.Config.Skyboxs.TryGetValue(skyData.Skybox, out var skybox))
    {
      _plugin.EnvManager.SetSkybox(player.Slot, skybox);
    }

    _plugin.EnvManager.SetBrightness(player.Slot, skyData.Brightness);

    if (skyData.Color != int.MaxValue)
    {
      _plugin.EnvManager.SetTintColor(player.Slot, Color.FromArgb(skyData.Color));
    }
  }

  // ── Persistence ─────────────────────────────────────────────────────────────

  public void Save(ulong? steamid = null)
  {
    _storage.Save(steamid);
  }

  public Task SaveAsync(ulong steamid)
  {
    return _storage.SaveAsync(steamid);
  }

  /// <summary>Removes the player's data from the in-memory cache so the next
  /// access forces a fresh database load.</summary>
  public void InvalidateCache(ulong steamid)
  {
    _storage.InvalidateCache(steamid);
  }

  /// <summary>Loads a single player's row from the database into the cache.</summary>
  public Task LoadPlayerAsync(ulong steamid)
  {
    return _storage.LoadPlayerAsync(steamid);
  }
}
