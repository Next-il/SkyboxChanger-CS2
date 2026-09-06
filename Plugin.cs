using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using MenuManager;
using PanoramaManager;

namespace SkyboxChanger;

public class SkyboxChanger : BasePlugin, IPluginConfig<SkyboxConfig>
{
  public override string ModuleName => "Skybox Changer";
  public override string ModuleVersion => "1.5.0";
  public override string ModuleAuthor => "samyyc (fork by luca.uy)";

  public SkyboxConfig Config { get; set; } = new();

  public required EnvManager EnvManager { get; set; } = new();

  public required Service Service { get; set; }

  public required SpectatorSkyboxManager SpectatorManager { get; set; }

  // MenuManager capability. Optional now: the Panorama panel is the primary UI and these menus
  // are the fallback for a server without the Panorama natives.
  private IMenuApi? _menuApi;
  private readonly PluginCapability<IMenuApi?> _menuCapability = new("menu:nfcore");

  private SkyboxPanel? _skyboxPanel;

  private static SkyboxChanger? _Instance { get; set; }

  public override unsafe void Load(bool hotReload)
  {
    if (hotReload)
    {
      Logger.LogError("HOT RELOAD DETECTED. It's NOT recommended to hot reload this plugin, please restart your server.");
    }
    KvLib.SetDllImportResolver();
    MemoryManager.Load();
    _Instance = this;

    SpectatorManager = new SpectatorSkyboxManager(this);

    // Once, here - not from Initialize, which runs on every map start.
    SpectatorManager.RegisterGameEvents();

    RegisterListener<Listeners.OnServerPrecacheResources>(OnServerPrecacheResources);
    RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
    RegisterListener<Listeners.OnMapStart>((map) =>
    {
      Server.NextFrame(() =>
      {
        foreach (var fog in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("env_cubemap_fog"))
        {
          if (fog != null && fog.IsValid)
          {
            fog.Remove();
          }
        }
      });
      if (!Config.Skyboxs.ContainsKey(""))
      {
        var skybox = Service.GetMapDefaultSkybox(map);
        if (skybox != null)
        {
          var defaultSkybox = new Skybox
          {
            Name = Localizer["menu.defaultskybox"],
            Material = skybox.Material,
          };
          Config.Skyboxs.Add("", defaultSkybox);
          EnvManager.DefaultMaterial = skybox.Material;
        }
      }
      SpectatorManager.Initialize();
    });
    RegisterListener<Listeners.OnMapEnd>(() =>
    {
      SpectatorManager.Shutdown();
      EnvManager.Shutdown();
      Service.Save();
      MemoryManager.RemoveCachedFactory();
    });
    RegisterListener<Listeners.OnServerPreFatalShutdown>(() =>
    {
      SpectatorManager.Shutdown();
      Service.Save();
    });
    RegisterListener<Listeners.OnEntityCreated>((entity) =>
    {
      Server.NextFrame(() =>
      {
        if (entity.DesignerName == "env_cubemap_fog")
        {
          // CEnvCubemapFog fog = new CEnvCubemapFog(entity.Handle);
          // EnvManager.CubemapFogPointedSkyName = "[PR#]" + fog.SkyEntity;
          entity.Remove();
          return;
        }
        if (entity.DesignerName == "env_sky")
        {
          CEnvSky sky = new CEnvSky(entity.Handle);
          if (entity.PrivateVScripts == null || !entity.PrivateVScripts.StartsWith("skyboxchanger_"))
          {
            nint materialptr = *(IntPtr*)sky.SkyMaterial.Value;
            var GetMaterialName = VirtualFunction.Create<IntPtr, string>(materialptr, 0);
            string skyMaterial = GetMaterialName.Invoke(materialptr);

            // Capture the spawn group while a map entity still exists. Maps without a
            // sky_camera have no other source, and the keyvalue spawn path needs it.
            if (EnvManager.MapSpawnGroupHandle == 0)
            {
              EnvManager.MapSpawnGroupHandle = Helper.GetSpawnGroup(sky);
            }

            if (!Config.Skyboxs.ContainsKey(""))
            {
              EnvManager.DefaultMaterial = skyMaterial;
              Logger.LogInformation("[SkyboxChanger] Map default sky material is '{Material}'", skyMaterial);
              Config.Skyboxs.Add(
                "",
                new Skybox { Name = Localizer["menu.defaultskybox"], Material = skyMaterial }
              );
            }

            // Never probe the material system here. FindOrCreateMaterialFromResource is a
            // *create* call and the index we have lands on the wrong function, which corrupts
            // the map's sky into the missing-texture material just by being called.
            if (!Config.Enabled) return;

            sky.Remove();
          }
          else
          {
            EnvManager.SpawnedSkyboxes.Add(int.Parse(entity.PrivateVScripts.Replace("skyboxchanger_", "")), (int)entity.Index);
          }
        }
      });
    });
    RegisterEventHandler<EventPlayerConnectFull>((@event, info) =>
    {
      var slot = @event.Userid!.Slot;
      Server.NextFrame(() =>
      {
        foreach (var sky in Utilities.FindAllEntitiesByDesignerName<CEnvSky>("env_sky"))
        {
          if (Helper.IsPlayerSkybox(slot, sky))
          {
            sky.Remove();
            EnvManager.SpawnedSkyboxes.Remove(slot);
          }
        }
        LoadThenInitialize(slot);
      });
      return HookResult.Continue;
    });
    RegisterListener<Listeners.OnClientDisconnect>(slot =>
    {
      EnvManager.OnPlayerLeave(slot);
      SpectatorManager.OnPlayerDisconnect(slot);
      var player = Utilities.GetPlayerFromSlot(slot);
      if (player != null && player.IsValid)
      {
        // PanelHandle drops its own session on disconnect but raises no Close, so ours has to be
        // dropped by hand or it survives for the life of the process.
        _skyboxPanel?.Forget(player.SteamID);
      }
      if (player != null && player.AuthorizedSteamID != null && Service != null)
      {
        Service.Save(player.AuthorizedSteamID.SteamId64);
        Service.InvalidateCache(player.AuthorizedSteamID.SteamId64);
      }
    });
    Helper.Initialize();
  }

  public override void OnAllPluginsLoaded(bool hotReload)
  {
    // Here and not in Load: plugin load order is undefined, so in Load MenuManager may not have
    // registered its capability yet.
    _menuApi = _menuCapability.Get();

    if (_menuApi == null)
    {
      // A warning, not a fatal error. MenuManager used to be mandatory and its absence unloaded the
      // plugin; the Panorama panel is the primary UI now, so the only thing missing is the fallback
      // for servers that cannot run the panel.
      Logger.LogWarning(
        "[SkyboxChanger] MenuManager was not found. The Panorama panel still works; there is no chat-menu fallback for servers without the Panorama natives.");
    }

    InitPanel();
  }

  /// <summary>
  /// Brings up the Panorama card, once.
  ///
  /// <para>Panorama.Init is itself idempotent, but Spawn is not: a second Spawn creates a second
  /// custom_hud_layout entity and orphans the first, which then holds input capture that nothing
  /// still alive knows how to release. So the guard is on the handle rather than on Init.</para>
  /// </summary>
  private void InitPanel()
  {
    if (_skyboxPanel != null) return;

    try
    {
      Panorama.Init(this);

      _skyboxPanel = new SkyboxPanel(this);

      if (!Panorama.CanReceiveClicks)
      {
        Console.WriteLine("[SkyboxChanger] Panorama has no click channel - the panel will render but not respond. Run css_panorama_diag.");
      }
    }
    catch (Exception ex)
    {
      // A panel that cannot spawn must not take the plugin down with it. The skyboxes still apply
      // on connect and SkyboxCommand already falls back to the chat menu on its own.
      Console.WriteLine($"[SkyboxChanger] Failed to start the Panorama panel: {ex.Message}");

      _skyboxPanel?.Dispose();
      _skyboxPanel = null;
    }
  }

  private void OnCheckTransmit(CCheckTransmitInfoList infoList)
  {
    EnvManager.OnCheckTransmit(infoList);
  }

  public override void Unload(bool hotReload)
  {
    if (_menuApi != null)
    {
      foreach (var player in Utilities.GetPlayers().Where(p => p.IsValid))
      {
        _menuApi.CloseMenu(player);
      }
    }

    _skyboxPanel?.Dispose();
    _skyboxPanel = null;
    Panorama.Shutdown();

    SpectatorManager.Shutdown();
    Service.Save();
    MemoryManager.Unload();
    _menuApi = null;
  }

  public static SkyboxChanger GetInstance()
  {
    if (_Instance == null)
    {
      throw new Exception("SkyboxChanger is not loaded");
    }

    return _Instance;
  }


  public void OnConfigParsed(SkyboxConfig config)
  {
    Config = config;
    Service = new Service(this, Config.Database.Host, Config.Database.Port, Config.Database.User, Config.Database.Password, Config.Database.Database, Config.Database.TablePrefix);
  }

  /// <summary>
  /// Loads the player's saved row, then spawns their sky - waiting for Steam validation if it has
  /// not landed yet.
  ///
  /// <para>The old code initialized immediately whenever AuthorizedSteamID was still null and never
  /// came back to it. That is not harmless for a RECONNECT: OnClientDisconnect drops the player's
  /// cached row, so the fall-through read an empty cache, minted a default and gave them the map's
  /// own sky for the rest of the map with nothing that would ever retry. AuthorizedSteamID stays
  /// null until SteamAPI validation completes, which player_connect_full does not guarantee.</para>
  ///
  /// <para>Re-resolved from the slot on each retry rather than holding the controller, so a player
  /// who left mid-wait cannot have their load applied to whoever took the slot: the load is keyed
  /// off the steamid of whoever is in it now.</para>
  /// </summary>
  private void LoadThenInitialize(int slot, int attempt = 0)
  {
    if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } player) return;

    // Bots never authorize, so waiting on them is ten seconds of retries and a warning per bot.
    if (player.IsBot)
    {
      EnvManager.InitializeSkyboxForPlayer(player);
      return;
    }

    if (player.AuthorizedSteamID is { } authorized)
    {
      Service?.InvalidateCache(authorized.SteamId64);
      _ = LoadPlayerSettingsOnConnectAndInitialize(authorized.SteamId64, player);
      return;
    }

    // ~10s of retries, then give them a sky rather than none at all.
    if (attempt >= 20)
    {
      Logger.LogWarning(
        "[SkyboxChanger] slot {Slot} never authorized; applying the map default without a load", slot);
      EnvManager.InitializeSkyboxForPlayer(player);
      return;
    }

    AddTimer(0.5f, () => LoadThenInitialize(slot, attempt + 1));
  }

  private async Task LoadPlayerSettingsOnConnectAndInitialize(ulong steamId64, CCSPlayerController player)
  {
    try
    {
      await Service.LoadPlayerAsync(steamId64);
    }
    catch (Exception ex)
    {
      Logger.LogError("[SkyboxChanger] Failed to load settings for {SteamId}: {Error}", steamId64, ex.Message);
    }

    Server.NextFrame(() =>
    {
      if (!player.IsValid) return;
      EnvManager.InitializeSkyboxForPlayer(player);
    });
  }

  public void OnServerPrecacheResources(ResourceManifest manifest)
  {
    Logger.LogInformation("[SkyboxChanger] Precaching {Count} skybox material(s)", Config.Skyboxs.Count);
    foreach (var skybox in Config.Skyboxs)
    {
      if (skybox.Value.Name == "")
      {
        skybox.Value.Name = skybox.Key;
      }
      manifest.AddResource(skybox.Value.Material);
    }
  }

  [ConsoleCommand("css_sky")]
  [ConsoleCommand("css_skybox")]
  [CommandHelper(0, "Change skybox", CommandUsage.CLIENT_ONLY)]
  public unsafe void SkyboxCommand(CCSPlayerController player, CommandInfo info)
  {
    if (Config.MenuPermission != "" && Config.MenuPermission != null && !AdminManager.PlayerHasPermissions(player, [Config.MenuPermission]))
    {
      player.PrintToChat($"{Localizer["prefix"]} {Localizer["no.permission"]}");
      return;
    }

    if (SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
    {
      player.PrintToChat($"{Localizer["prefix"]} {Localizer["need.alive"]}");
      return;
    }

    // The panel first, the chat menus as the fallback. Open returns false when the player cannot be
    // shown a card at all - no per-player text natives, or the render threw - which is exactly the
    // case where the old menus are still the better answer.
    if (_skyboxPanel?.Open(player) == true) return;

    if (_menuApi == null)
    {
      player.PrintToChat($"{Localizer["prefix"]} {Localizer["menu.error"]}");
      return;
    }

    ShowMainMenu(player);
  }

  private void ShowMainMenu(CCSPlayerController player)
  {
    if (_menuApi == null) return;

    var mainMenu = _menuApi.GetMenu(Localizer["menu.title"]);

    mainMenu.AddMenuOption(Localizer["menu.skybox"], (p, option) =>
    {
      ShowSkyboxMenu(p);
    });

    mainMenu.AddMenuOption(Localizer["menu.brightness"], (p, option) =>
    {
      ShowBrightnessMenu(p);
    });

    mainMenu.AddMenuOption(Localizer["menu.tintcolor"], (p, option) =>
    {
      ShowColorMenu(p);
    });

    mainMenu.Open(player);
  }

  private void ShowSkyboxMenu(CCSPlayerController player)
  {
    if (_menuApi == null) return;

    if (SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
    {
      player.PrintToChat($"{Localizer["prefix"]} {Localizer["spectator.cannot_change"]}");
      return;
    }

    var skyboxMenu = _menuApi.GetMenu(Localizer["menu.title"]);

    var skyboxes = Config.Skyboxs.ToList();
    skyboxes.RemoveAll(kv => kv.Key == "");
    if (Config.Skyboxs.ContainsKey(""))
    {
      var def = Config.Skyboxs[""];
      skyboxes.Insert(0, new KeyValuePair<string, Skybox>("", def));
    }

    skyboxes.ForEach(skybox =>
    {
      if (!Helper.PlayerHasPermission(player, skybox.Value.Permissions, skybox.Value.PermissionsOr)) return;

      skyboxMenu.AddMenuOption(skybox.Value.Name, (p, option) =>
      {
        var result = Service.SetSkybox(p, skybox.Key);
        if (result)
        {
          p.PrintToChat($"{Localizer["prefix"]} {Localizer["change.success"]}");
        }
        else
        {
          p.PrintToChat($"{Localizer["prefix"]} {Localizer["change.failed"]}");
        }
        // _menuApi?.CloseMenu(p);
      });
    });

    skyboxMenu.AddMenuOption("← " + Localizer["menu.back"], (p, option) =>
    {
      ShowMainMenu(p);
    });

    skyboxMenu.Open(player);
  }

  private void ShowBrightnessMenu(CCSPlayerController player)
  {
    if (_menuApi == null) return;

    if (SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
    {
      player.PrintToChat($"{Localizer["prefix"]} {Localizer["spectator.cannot_change"]}");
      return;
    }

    var brightnessMenu = _menuApi.GetMenu(Localizer["menu.brightness"]);

    float currentBrightness = Service.GetPlayerBrightness(player);

    brightnessMenu.AddMenuOption("-- (- 0.5)", (p, option) =>
    {
      float newValue = Math.Max(0.0f, currentBrightness - 0.5f);
      Service.SetBrightness(p, newValue);
      ShowBrightnessMenu(p);
    });

    brightnessMenu.AddMenuOption("- (- 0.1)", (p, option) =>
    {
      float newValue = Math.Max(0.0f, currentBrightness - 0.1f);
      Service.SetBrightness(p, newValue);
      ShowBrightnessMenu(p);
    });

    brightnessMenu.AddMenuOption($"{Localizer["menu.current"]}: {currentBrightness:F1}", (p, option) =>
    {
      // Do nothing, just display
    });

    brightnessMenu.AddMenuOption("+ (+ 0.1)", (p, option) =>
    {
      float newValue = Math.Min(10.0f, currentBrightness + 0.1f);
      Service.SetBrightness(p, newValue);
      ShowBrightnessMenu(p);
    });

    brightnessMenu.AddMenuOption("++ (+ 0.5)", (p, option) =>
    {
      float newValue = Math.Min(10.0f, currentBrightness + 0.5f);
      Service.SetBrightness(p, newValue);
      ShowBrightnessMenu(p);
    });

    brightnessMenu.AddMenuOption("← " + Localizer["menu.back"], (p, option) =>
    {
      ShowMainMenu(p);
    });

    brightnessMenu.Open(player);
  }

  private void ShowColorMenu(CCSPlayerController player)
  {
    if (_menuApi == null) return;

    if (SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
    {
      player.PrintToChat($"{Localizer["prefix"]} {Localizer["spectator.cannot_change"]}");
      return;
    }

    var colorMenu = _menuApi.GetMenu(Localizer["menu.tintcolor"]);

    foreach (var knownColor in (KnownColor[])Enum.GetValues(typeof(KnownColor)))
    {
      if (Color.FromKnownColor(knownColor).IsSystemColor) continue;

      colorMenu.AddMenuOption(knownColor.ToString(), (p, option) =>
      {
        Service.SetTintColor(p, Color.FromKnownColor(knownColor));
        // _menuApi?.CloseMenu(p);
      });
    }

    colorMenu.AddMenuOption("← " + Localizer["menu.back"], (p, option) =>
    {
      ShowMainMenu(p);
    });

    colorMenu.Open(player);
  }
}