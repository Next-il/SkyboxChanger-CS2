using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using Microsoft.Extensions.Logging;

namespace SkyboxChanger;

public class Helper
{
  public static bool IsPlayerSkybox(int slot, CEnvSky sky)
  {
    return slot == -1 || sky.PrivateVScripts == "skyboxchanger_" + slot;
  }

  public static void Initialize()
  {

  }

  /// <summary>Reads the spawn group an entity belongs to out of its CEntityIdentity.</summary>
  public static unsafe uint GetSpawnGroup(CBaseEntity entity)
  {
    if (entity.Entity == null) return 0;
    return *(uint*)(entity.Entity.Handle + 0x34);
  }

  /// <summary>Spawns the player's env_sky carrying the material as a "skyname" keyvalue, which
  /// the engine resolves during the spawn-group load. A sky_camera is only one source of the
  /// spawn group; maps without a 3D skybox have none, so fall back to the handle captured from
  /// the map's own env_sky at load.</summary>
  public static void SpawnSkybox(int slot, string fogTargetName, string material)
  {
    var instance = SkyboxChanger.GetInstance();

    var skycameras = Utilities.FindAllEntitiesByDesignerName<CSkyCamera>("sky_camera");
    uint spawngrouphandle = skycameras.Count() != 0
      ? GetSpawnGroup(skycameras.First())
      : instance.EnvManager.MapSpawnGroupHandle;

    if (spawngrouphandle == 0)
    {
      instance.Logger.LogError("[SkyboxChanger] SpawnSkybox: no spawn group handle for slot={Slot}; cannot spawn a sky carrying a material", slot);
      return;
    }

    MemoryManager.CreateLoadingSpawnGroupAndSpawnEntities(spawngrouphandle, true, true, KvLib.MakeKeyValue(fogTargetName, "skyboxchanger_" + slot, material));
  }

  /// <summary>Destroys the player's env_sky so a fresh one can take its place.</summary>
  public static void RemovePlayerSkybox(int slot)
  {
    var env = SkyboxChanger.GetInstance().EnvManager;
    if (!env.SpawnedSkyboxes.TryGetValue(slot, out var index)) return;
    env.SpawnedSkyboxes.Remove(slot);
    var sky = Utilities.GetEntityFromIndex<CEnvSky>(index);
    if (sky != null && sky.IsValid) sky.Remove();
  }

  /// <summary>Applies a skybox by respawning the player's env_sky with the new material.
  /// The material can only be set at spawn, so changing it means replacing the entity.</summary>
  public static bool RespawnSkybox(int slot, Skybox skybox)
  {
    var instance = SkyboxChanger.GetInstance();
    var env = instance.EnvManager;

    var player = Utilities.GetPlayerFromSlot(slot);
    if (player == null || !player.IsValid)
    {
      instance.Logger.LogError("[SkyboxChanger] RespawnSkybox: no valid player for slot={Slot}", slot);
      return false;
    }

    // The spawn keyvalues hardcode brightness 1.0 and a white tint, so the player's own
    // values have to be reapplied once the new entity exists.
    float brightness = skybox.Brightness ?? instance.Service.GetPlayerBrightness(player);
    Color color = instance.Service.GetPlayerColor(player);

    RemovePlayerSkybox(slot);
    SpawnSkybox(slot, env.CubemapFogPointedSkyName ?? "", skybox.Material);

    // OnEntityCreated registers the new entity a frame later.
    Server.NextFrame(() => Server.NextFrame(() =>
    {
      if (!env.SpawnedSkyboxes.ContainsKey(slot))
      {
        instance.Logger.LogError("[SkyboxChanger] RespawnSkybox: env_sky never registered for slot={Slot}", slot);
        return;
      }
      ChangeSkybox(slot, brightness, color.ToArgb() == int.MaxValue ? null : color);
    }));

    return true;
  }

  /// <summary>Updates brightness and tint on the player's existing env_sky. The material is
  /// deliberately not settable here: it is a resource handle that can only be bound at spawn.</summary>
  public static bool ChangeSkybox(int slot, float? brightness = null, Color? color = null)
  {
    var instance = SkyboxChanger.GetInstance();
    if (!instance.EnvManager.SpawnedSkyboxes.TryGetValue(slot, out var entityIndex))
    {
      return false;
    }

    var sky = Utilities.GetEntityFromIndex<CEnvSky>(entityIndex);
    if (sky == null) return false;

    if (color != null) sky.TintColor = (Color)color;
    if (brightness != null) sky.BrightnessScale = brightness.Value;

    Utilities.SetStateChanged(sky, "CEnvSky", "m_vTintColor");
    Utilities.SetStateChanged(sky, "CEnvSky", "m_flBrightnessScale");
    return true;
  }

  // CanUseSkybox lived here and combined Config.MenuPermission with the skybox's own permissions.
  // It is gone on purpose: its only caller was the connect-time restore, where the menu permission
  // is both wrong (wearing a saved sky is not opening the menu) and racy - it is read one frame
  // after player_connect_full, while CS2-SimpleAdmin is still re-adding its admins asynchronously,
  // and a false answer there dropped the player's saved sky for the whole map. Gate the MENU in
  // SkyboxCommand; gate the SKY with PlayerHasPermission.

  public static bool PlayerHasPermission(CCSPlayerController player, string[]? permissions, string[]? permissionsOr)
  {

    if (permissions != null)
    {
      foreach (string perm in permissions)
      {
        if (perm.StartsWith("@"))
        {
          if (!AdminManager.PlayerHasPermissions(player, [perm]))
          {
            return false;
          }
        }
        else if (perm.StartsWith("#"))
        {
          if (!AdminManager.PlayerInGroup(player, [perm]))
          {
            return false;
          }
        }
        else
        {
          ulong steamId;
          if (!ulong.TryParse(perm, out steamId))
          {
            throw new FormatException($"Unknown SteamID64 format: {perm}");
          }
          else
          {
            if (player.SteamID != steamId)
            {
              return false;
            }
          }
        }
      }
    }

    if (permissionsOr != null)
    {
      foreach (string perm in permissionsOr)
      {
        if (perm.StartsWith("@"))
        {
          if (AdminManager.PlayerHasPermissions(player, perm))
          {
            return true;
          }
        }
        else if (perm.StartsWith("#"))
        {
          if (AdminManager.PlayerInGroup(player, perm))
          {
            return true;
          }
        }
        else
        {
          ulong steamId;
          if (!ulong.TryParse(perm, out steamId))
          {
            throw new FormatException($"Unknown SteamID64 format: {perm}");
          }
          else
          {
            if (player.SteamID == steamId)
            {
              return true;
            }
          }
        }
      }
    }

    return permissionsOr == null || permissionsOr.Length == 0;
  }
}
