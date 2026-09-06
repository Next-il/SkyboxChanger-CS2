using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace SkyboxChanger;

public class SpectatorSkyboxManager
{
    private readonly Dictionary<int, int> _spectatorToTarget = new();
    private readonly Dictionary<int, bool> _isSpectatorMode = new();
    private CounterStrikeSharp.API.Modules.Timers.Timer? _updateTimer;
    private SkyboxChanger _plugin;

    public SpectatorSkyboxManager(SkyboxChanger plugin)
    {
        _plugin = plugin;
    }

    /// <summary>Starts the per-map polling timer. Called from OnMapStart, so it must NOT register
    /// event handlers - see <see cref="RegisterGameEvents"/>.</summary>
    public void Initialize()
    {
        _updateTimer = _plugin.AddTimer(0.5f, CheckSpectatorUpdates, TimerFlags.REPEAT);
    }

    /// <summary>
    /// Hooks the four events this manager reacts to. Called once, from the plugin's Load.
    ///
    /// <para>It used to be called from Initialize, i.e. from OnMapStart. CSS does not dedupe
    /// handlers, so after N maps every spawn, death, team change and connect ran
    /// CheckSpectatorUpdates N times, each pass walking every player.</para>
    /// </summary>
    public void RegisterGameEvents()
    {
        _plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        _plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        _plugin.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        _plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player?.IsValid == true)
        {
            Server.NextFrame(() =>
            {
                if (_isSpectatorMode.ContainsKey(player.Slot))
                {
                    RestoreSpectatorSkybox(player.Slot);
                    _spectatorToTarget.Remove(player.Slot);
                    _isSpectatorMode.Remove(player.Slot);
                }

                CheckSpectatorUpdates();
            });
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player?.IsValid == true)
        {
            Server.NextFrame(() =>
            {
                _plugin.AddTimer(0.1f, () =>
                {
                    CheckSpectatorUpdates();
                }, TimerFlags.STOP_ON_MAPCHANGE);
            });
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player?.IsValid == true)
        {
            Server.NextFrame(() =>
            {
                if (_isSpectatorMode.ContainsKey(player.Slot))
                {
                    RestoreSpectatorSkybox(player.Slot);
                    _spectatorToTarget.Remove(player.Slot);
                    _isSpectatorMode.Remove(player.Slot);
                }

                CheckSpectatorUpdates();
            });
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player?.IsValid == true)
        {
            Server.NextFrame(() =>
            {
                OnPlayerDisconnect(player.Slot);
                CheckSpectatorUpdates();
            });
        }
        return HookResult.Continue;
    }

    public void Shutdown()
    {
        _updateTimer?.Kill();
        _updateTimer = null;

        foreach (var spectatorSlot in _isSpectatorMode.Keys.ToList())
        {
            RestoreSpectatorSkybox(spectatorSlot);
        }

        _spectatorToTarget.Clear();
        _isSpectatorMode.Clear();
    }

    private void CheckSpectatorUpdates()
    {
        try
        {
            var allPlayers = Utilities.GetPlayers().Where(p => p.IsValid).ToList();
            var currentSpectatorMappings = new Dictionary<int, int>();

            foreach (var player in allPlayers)
            {
                if (!player.IsValid)
                    continue;

                var targetSlot = GetSpectatingTarget(player);
                if (targetSlot.HasValue)
                {
                    currentSpectatorMappings[player.Slot] = targetSlot.Value;
                }
            }

            foreach (var kvp in currentSpectatorMappings)
            {
                var spectatorSlot = kvp.Key;
                var targetSlot = kvp.Value;

                if (!_spectatorToTarget.ContainsKey(spectatorSlot) ||
                    _spectatorToTarget[spectatorSlot] != targetSlot)
                {
                    ApplyTargetSkybox(spectatorSlot, targetSlot);
                    _spectatorToTarget[spectatorSlot] = targetSlot;
                    _isSpectatorMode[spectatorSlot] = true;
                }
            }

            foreach (var spectatorSlot in _spectatorToTarget.Keys.ToList())
            {
                if (!currentSpectatorMappings.ContainsKey(spectatorSlot))
                {
                    RestoreSpectatorSkybox(spectatorSlot);
                    _spectatorToTarget.Remove(spectatorSlot);
                    _isSpectatorMode.Remove(spectatorSlot);
                }
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[SkyboxChanger] Error in CheckSpectatorUpdates: {ex.Message}");
        }
    }

    private int? GetSpectatingTarget(CCSPlayerController spectator)
    {
        if (spectator?.IsValid != true)
            return null;

        try
        {
            if (spectator.PawnIsAlive)
                return null;

            if (spectator.PlayerPawn?.Value != null)
            {
                var observerServices = spectator.PlayerPawn.Value.ObserverServices;
                if (observerServices?.ObserverTarget?.Value != null)
                {
                    var targetPawn = observerServices.ObserverTarget.Value;
                    var targetPlayer = Utilities.GetPlayers()
                        .FirstOrDefault(p => p.IsValid && p.PlayerPawn?.Value?.Handle == targetPawn.Handle);

                    if (targetPlayer != null && targetPlayer.Slot != spectator.Slot && targetPlayer.PawnIsAlive)
                    {
                        return targetPlayer.Slot;
                    }
                }
            }

            if (spectator.ObserverPawn?.Value != null)
            {
                var observerServices = spectator.ObserverPawn.Value.ObserverServices;
                if (observerServices?.ObserverTarget?.Value != null)
                {
                    var targetPawn = observerServices.ObserverTarget.Value;
                    var targetPlayer = Utilities.GetPlayers()
                        .FirstOrDefault(p => p.IsValid && p.PlayerPawn?.Value?.Handle == targetPawn.Handle);

                    if (targetPlayer != null && targetPlayer.Slot != spectator.Slot && targetPlayer.PawnIsAlive)
                    {
                        return targetPlayer.Slot;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[SkyboxChanger] Error in GetSpectatingTarget for slot {spectator.Slot}: {ex.Message}");
        }

        return null;
    }

    private void ApplyTargetSkybox(int spectatorSlot, int targetSlot)
    {
        try
        {
            var spectatorPlayer = Utilities.GetPlayerFromSlot(spectatorSlot);
            var targetPlayer = Utilities.GetPlayerFromSlot(targetSlot);

            if (spectatorPlayer?.IsValid != true || targetPlayer?.IsValid != true)
                return;

            var instance = SkyboxChanger.GetInstance();

            var targetSkybox = instance.Service.GetPlayerSkybox(targetPlayer);
            var targetBrightness = instance.Service.GetPlayerBrightness(targetPlayer);
            var targetColor = instance.Service.GetPlayerColor(targetPlayer);

            if (targetSkybox != null)
            {
                instance.EnvManager.SetSkybox(spectatorSlot, targetSkybox);
            }
            else
            {
                var defaultSkybox = instance.Config.Skyboxs.GetValueOrDefault("");
                if (defaultSkybox != null)
                {
                    instance.EnvManager.SetSkybox(spectatorSlot, defaultSkybox);
                }
            }

            instance.EnvManager.SetBrightness(spectatorSlot, targetBrightness);
            instance.EnvManager.SetTintColor(spectatorSlot, targetColor);

            Server.PrintToConsole($"[SkyboxChanger] Applied target skybox: Spectator {spectatorSlot} -> Target {targetSlot}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[SkyboxChanger] Error in ApplyTargetSkybox: {ex.Message}");
        }
    }

    private void RestoreSpectatorSkybox(int spectatorSlot)
    {
        try
        {
            var spectatorPlayer = Utilities.GetPlayerFromSlot(spectatorSlot);
            if (spectatorPlayer?.IsValid != true)
                return;

            var instance = SkyboxChanger.GetInstance();

            var originalSkybox = instance.Service.GetPlayerSkybox(spectatorPlayer);
            var originalBrightness = instance.Service.GetPlayerBrightness(spectatorPlayer);
            var originalColor = instance.Service.GetPlayerColor(spectatorPlayer);

            if (originalSkybox != null)
            {
                instance.EnvManager.SetSkybox(spectatorSlot, originalSkybox);
            }
            else
            {
                var defaultSkybox = instance.Config.Skyboxs.GetValueOrDefault("");
                if (defaultSkybox != null)
                {
                    instance.EnvManager.SetSkybox(spectatorSlot, defaultSkybox);
                }
            }

            instance.EnvManager.SetBrightness(spectatorSlot, originalBrightness);
            instance.EnvManager.SetTintColor(spectatorSlot, originalColor);

            Server.PrintToConsole($"[SkyboxChanger] Restored original skybox for slot {spectatorSlot}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[SkyboxChanger] Error in RestoreSpectatorSkybox: {ex.Message}");
        }
    }

    public bool IsPlayerInSpectatorMode(int slot)
    {
        return _isSpectatorMode.GetValueOrDefault(slot, false);
    }

    public void OnPlayerDisconnect(int slot)
    {
        _spectatorToTarget.Remove(slot);
        _isSpectatorMode.Remove(slot);

        var spectatorsOfDisconnected = _spectatorToTarget
            .Where(kvp => kvp.Value == slot)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var spectatorSlot in spectatorsOfDisconnected)
        {
            RestoreSpectatorSkybox(spectatorSlot);
            _spectatorToTarget.Remove(spectatorSlot);
            _isSpectatorMode.Remove(spectatorSlot);
        }
    }
}