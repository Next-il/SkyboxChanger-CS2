using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using PanoramaManager;

namespace SkyboxChanger;

/// <summary>
/// The <c>css_sky</c> / <c>css_skybox</c> card: the skybox list, the brightness steps and the tint
/// swatches as a Panorama panel instead of three chat menus.
///
/// <para><b>Owns no skybox logic.</b> Applying goes through <see cref="Service.SetSkybox"/>,
/// <see cref="Service.SetBrightness"/> and <see cref="Service.SetTintColor"/> - the same calls the
/// chat menus make - so the two views can never disagree about what is applied, what is persisted
/// or who is allowed to do it.</para>
///
/// <para><b>One layout, fixed pools.</b> A CustomHud layout cannot create panels at runtime: the
/// server may only write dialog-variable strings and toggle classes on ids that already exist. So
/// every cell is authored up front and the unused ones carry <c>hidden</c>. <see cref="Cells"/>,
/// <see cref="GridRows"/> and <see cref="Swatches"/> are the same numbers and names as
/// <c>skybox_hud.xml</c>; changing one without the other silently drops cells with no error
/// anywhere.</para>
/// </summary>
internal sealed class SkyboxPanel : IDisposable
{
    private const string Layout = "panorama/layout/custom_game/skybox_hud.vxml_c";

    /// <summary>Grid shape in skybox_hud.xml: sb_r0..sb_r5, each holding three of sb0..sb17.</summary>
    private const int Cols = 3;
    private const int GridRows = 6;
    private const int Cells = Cols * GridRows;

    private enum Tab { Skybox, Brightness, Tint }

    /// <summary>
    /// What one player is currently looking at. Keyed by their SteamID rather than their slot,
    /// which is recycled the moment they leave.
    /// </summary>
    private sealed class Session
    {
        public Tab Tab;
        public int Page;

        /// <summary>
        /// Which config key sits in each visible cell, so a click on <c>sb7</c> resolves without
        /// trusting anything the client sent beyond the cell index - it can name a position on the
        /// page in front of it, never a key it was not shown.
        /// </summary>
        public readonly List<string> CellKeys = [];
    }

    /// <summary>
    /// The 24 tint swatches, in the order skybox_hud.xml lays them out (4 rows of 6).
    ///
    /// <para>Keyed by the button id rather than an index, so reordering the XML cannot silently
    /// repaint a swatch: <c>tc_gold</c> is gold in both files or it is nowhere. Each id also has a
    /// matching <c>tint-*</c> class in skybox_hud.css that paints the chip - the server cannot send
    /// a colour, so the palette is baked into the stylesheet and this table only names it.</para>
    /// </summary>
    private static readonly (string Id, KnownColor Color)[] Swatches =
    [
        ("tc_white", KnownColor.White), ("tc_silver", KnownColor.Silver),
        ("tc_gray", KnownColor.Gray), ("tc_black", KnownColor.Black),
        ("tc_maroon", KnownColor.Maroon), ("tc_red", KnownColor.Red),

        ("tc_crimson", KnownColor.Crimson), ("tc_pink", KnownColor.Pink),
        ("tc_magenta", KnownColor.Magenta), ("tc_purple", KnownColor.Purple),
        ("tc_indigo", KnownColor.Indigo), ("tc_navy", KnownColor.Navy),

        ("tc_blue", KnownColor.Blue), ("tc_skyblue", KnownColor.SkyBlue),
        ("tc_cyan", KnownColor.Cyan), ("tc_teal", KnownColor.Teal),
        ("tc_seagreen", KnownColor.SeaGreen), ("tc_green", KnownColor.Green),

        ("tc_lime", KnownColor.Lime), ("tc_yellow", KnownColor.Yellow),
        ("tc_gold", KnownColor.Gold), ("tc_orange", KnownColor.Orange),
        ("tc_brown", KnownColor.Brown), ("tc_tan", KnownColor.Tan),
    ];

    private readonly SkyboxChanger _plugin;
    private readonly PanelHandle _panel;
    private readonly Dictionary<ulong, Session> _sessions = [];

    /// <summary>Config keys whose permission list has already been reported as malformed, so a
    /// broken entry costs one log line rather than one per render per player.</summary>
    private readonly HashSet<string> _warnedKeys = [];

    public SkyboxPanel(SkyboxChanger plugin)
    {
        _plugin = plugin;

        _panel = Panorama.Spawn(Layout, new LayoutContract
        {
            // Unique server-wide. Dialog variables are addressed by interned panel id, so a root id
            // shared with another layout - in this plugin or any other - makes the two overwrite
            // each other's text.
            RootPanelId = "SkyboxRoot",
            RevealClass = "show",
            CloseButtonId = "sky_close",

            // No row0..rowN pool: the grid, the steps and the swatches are all addressed directly,
            // because each carries state classes rather than the title/subtitle a pooled row has.
            RowCount = 0,

            CaptureInput = true,

            // Per-viewer text: two players browsing at once are on different pages, wearing
            // different skyboxes and allowed different entries.
            SharedText = false,

            // The crosshair is drawn by the game's own HUD, which is not a sibling of this layout -
            // z-index only orders panels inside one parent, so no value can lift the card above it.
            // The library puts it back on close.
            HideHud = HideHudFlags.Crosshair,
        });

        _panel.OnEvent += OnEvent;
    }

    public void Dispose() => _panel.Dispose();

    /// <summary>
    /// Shows the card. Returns false when it cannot be shown, so the caller can fall back to the
    /// MenuManager menus rather than leaving the player with nothing.
    /// </summary>
    public bool Open(CCSPlayerController player)
    {
        if (player is not { IsValid: true, IsBot: false, IsHLTV: false }) return false;

        // Every word on this card is a per-player dialog variable. Without that native the layout
        // still renders - the frame, the borders, the grid lines are all CSS - but nothing has any
        // text in it, which reads as a broken menu rather than a missing capability. Refusing lets
        // the caller open the chat menu instead.
        if (!Panorama.CanWritePerPlayerText) return false;

        PruneSessions();

        Session session = new();
        _sessions[player.SteamID] = session;

        _panel.Title = Text("menu.title", "SKYBOXCHANGER");
        _panel.Open(player);

        // The handle has to have a session before per-player writes land, so this is after Open.
        if (!_panel.IsOpenFor(player))
        {
            _sessions.Remove(player.SteamID);
            return false;
        }

        // PanelHandle.Open guards its own first draw and closes the panel if it throws, because
        // input capture is taken before anything is drawn and a half-drawn card leaves the player
        // with a cursor over nothing. This render runs after that guard, so it needs the same
        // treatment: failing back to false lets the caller open the chat menu.
        return TryRender(player, session);
    }

    /// <summary>
    /// Drops a leaving player's session.
    ///
    /// <para>PanelHandle drops its own session on disconnect but raises no Close event, so the
    /// handler in <see cref="OnEvent"/> never fires for someone who leaves with the card open and
    /// the entry survives until the next <see cref="Open"/> prunes it - which may be never.</para>
    /// </summary>
    public void Forget(ulong steamId) => _sessions.Remove(steamId);

    /// <summary>
    /// Drops sessions for players who are gone. Close clears the normal case, but a disconnect with
    /// the card open never reaches it, so without this the dictionary grows by one per such
    /// disconnect for the life of the process.
    /// </summary>
    private void PruneSessions()
    {
        if (_sessions.Count == 0) return;

        HashSet<ulong> live = [.. Connected().Select(p => p.SteamID)];

        foreach (ulong steamId in _sessions.Keys.Where(k => !live.Contains(k)).ToList())
            _sessions.Remove(steamId);
    }

    private static IEnumerable<CCSPlayerController> Connected() =>
        Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false });

    // ------------------------------------------------------------------ the list

    /// <summary>
    /// The skyboxes this player is allowed to see, Default first and then config order - the same
    /// ordering ShowSkyboxMenu builds.
    ///
    /// <para><b>Built fresh on every render, never cached.</b> The <c>""</c> Default entry is added
    /// by OnMapStart / OnEntityCreated and removed again by EnvManager.Shutdown on map end, so a
    /// list held across a map change would offer a key that no longer exists.</para>
    ///
    /// <para>Filtering happens HERE rather than at draw time so a denied entry never occupies a
    /// cell: paging over the filtered list means no holes and no page that is empty for one player
    /// and full for another.</para>
    /// </summary>
    private List<KeyValuePair<string, Skybox>> Visible(CCSPlayerController player)
    {
        List<KeyValuePair<string, Skybox>> all = [.. _plugin.Config.Skyboxs];
        all.RemoveAll(kv => kv.Key == "");

        if (_plugin.Config.Skyboxs.TryGetValue("", out Skybox? def))
            all.Insert(0, new KeyValuePair<string, Skybox>("", def));

        return [.. all.Where(kv => Allowed(player, kv.Key, kv.Value))];
    }

    /// <summary>
    /// The per-skybox permission gate, exactly <see cref="Helper.PlayerHasPermission"/> - including
    /// its trailing "no permissionsOr means allowed" rule - and applied both when building the list
    /// and again on the click, so a group revoked while the card is open cannot be spent.
    ///
    /// <para><b>A throw is a denial, not a crash.</b> PlayerHasPermission raises FormatException on
    /// an entry that is neither <c>@</c> nor <c>#</c> nor a parseable SteamID64. In the chat menu
    /// that takes down the whole menu build; here one malformed config entry would blank the entire
    /// grid, so it costs its own entry and one log line instead.</para>
    /// </summary>
    private bool Allowed(CCSPlayerController player, string key, Skybox skybox)
    {
        try
        {
            return Helper.PlayerHasPermission(player, skybox.Permissions, skybox.PermissionsOr);
        }
        catch (Exception ex)
        {
            if (_warnedKeys.Add(key))
            {
                Console.WriteLine(
                    $"[SkyboxChanger] Skybox '{key}' has a malformed permission entry and is hidden from the panel: {ex.Message}");
            }

            return false;
        }
    }

    /// <summary>
    /// The menu permission, re-read on every click rather than trusted from open time.
    ///
    /// <para>The command already checks it, but an admin group can be revoked while the card is on
    /// screen - and a click is a request in its own right, not a continuation of the one that
    /// opened the panel.</para>
    /// </summary>
    private bool Authorised(CCSPlayerController player)
    {
        string? permission = _plugin.Config.MenuPermission;

        return string.IsNullOrEmpty(permission)
            || AdminManager.PlayerHasPermissions(player, [permission]);
    }

    /// <summary>
    /// The spectator gate the three chat submenus apply. Prints and returns true when the click has
    /// to be refused.
    ///
    /// <para>Spectator state is polled on a timer, so it flips underneath an open card. The panel is
    /// deliberately NOT closed on refusal - the old menus stayed open too, and a card that vanishes
    /// says less than a card that says why.</para>
    /// </summary>
    private bool Blocked(CCSPlayerController player)
    {
        if (!_plugin.SpectatorManager.IsPlayerInSpectatorMode(player.Slot)) return false;

        player.PrintToChat($"{_plugin.Localizer["prefix"]} {_plugin.Localizer["spectator.cannot_change"]}");
        return true;
    }

    // ------------------------------------------------------------------ render

    /// <summary>
    /// Redraws, or tears the card down. Every render past the library's own guarded first draw is
    /// reached with input capture already held, so a throw that escapes leaves the player pinned to
    /// a card that is blank or half-stale. Closing and dropping the session hands them back their
    /// mouse; Open reports it to the caller so the chat menu can take over.
    /// </summary>
    private bool TryRender(CCSPlayerController player, Session s)
    {
        try
        {
            Render(player, s);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkyboxChanger] Skybox panel render failed, falling back: {ex.Message}");

            _panel.Close(player);
            _sessions.Remove(player.SteamID);

            return false;
        }
    }

    /// <summary>
    /// Redraws everything this player can see.
    ///
    /// <para><b>All three views every time, not just the active one.</b> PanelAction.Restored hands
    /// back a rebuilt entity with every SetVariableFor and SetClassFor gone, so a render that drew
    /// only the visible tab would leave the other two structurally intact and completely blank the
    /// moment the player switched to them. Each call is a few dozen writes against panels that
    /// already exist, which is cheaper than tracking what is stale.</para>
    /// </summary>
    private void Render(CCSPlayerController player, Session s)
    {
        List<KeyValuePair<string, Skybox>> view = Visible(player);

        int pageCount = Math.Max(1, (int)Math.Ceiling(view.Count / (double)Cells));

        // Clamped against the list as it is NOW, not where the page was turned: a map change can
        // take the Default entry away, and a permission can be revoked, either of which can leave
        // the player on a page past the end with the pager as the only way back.
        s.Page = Math.Clamp(s.Page, 0, pageCount - 1);

        List<KeyValuePair<string, Skybox>> page = [.. view.Skip(s.Page * Cells).Take(Cells)];

        s.CellKeys.Clear();
        s.CellKeys.AddRange(page.Select(kv => kv.Key));

        RenderTabs(player, s);

        _panel.SetVariableFor(player, "menu_subtitle", $"{view.Count} {Text("panel.skyboxes", "SKYBOXES")}");
        _panel.SetVariableFor(player, "menu_footer",
            Text("panel.footer", "Pick a skybox, then tune its brightness and tint - all three are yours alone"));

        RenderGrid(player, s, page);

        // has-pages is not optional: .sky-pager is visibility:collapse without it, so a plugin that
        // never sets it has arrows nobody can reach. The library writes this too, from its own
        // (empty) item list - which is always one page - so ours has to come after, on every path
        // that reaches a library render: Open, and the Page and Restored handlers.
        _panel.SetClassFor(player, "SkyboxRoot", "has-pages", pageCount > 1);
        _panel.SetVariableFor(player, "menu_page", $"{Text("panel.page", "PAGE")} {s.Page + 1} / {pageCount}");

        RenderBrightness(player);
        RenderTint(player);
    }

    private void RenderGrid(CCSPlayerController player, Session s, List<KeyValuePair<string, Skybox>> page)
    {
        // Drawn from the key we just wrote to storage rather than read back off the entity:
        // SetSkybox respawns the env_sky over the following two frames, so the world does not agree
        // with the player's choice yet at the moment this runs.
        string current = _plugin.Service.GetPlayerSkyboxKey(player);

        _panel.SetVariableFor(player, "sb_title", Text("menu.skybox", "SELECT SKYBOX"));

        // Panorama cannot wrap, so the three-per-line split is a real panel per line. Hide the
        // lines this page does not fill or they leave a gap under the grid.
        for (int r = 0; r < GridRows; r++)
            _panel.SetClassFor(player, $"sb_r{r}", "hidden", page.Count <= r * Cols);

        for (int i = 0; i < Cells; i++)
        {
            string cell = $"sb{i}";

            if (i >= page.Count)
            {
                _panel.SetClassFor(player, cell, "hidden", true);

                // Left dressed as unselected so a cell reused by the next page cannot inherit the
                // previous occupant's highlight.
                _panel.SetClassFor(player, cell, "selected", false);
                continue;
            }

            (string key, Skybox skybox) = page[i];

            // Name, not key: the Default entry's key is the empty string, and a config may well
            // name an entry something friendlier than its key.
            _panel.SetVariableFor(player, cell, string.IsNullOrWhiteSpace(skybox.Name) ? key : skybox.Name);
            _panel.SetClassFor(player, cell, "hidden", false);
            _panel.SetClassFor(player, cell, "selected", key == current);
        }

        _panel.SetVariableFor(player, "sb_empty",
            Text("panel.empty", "No skyboxes are available to you on this server"));
        _panel.SetClassFor(player, "sb_empty", "hidden", page.Count != 0);
    }

    private void RenderBrightness(CCSPlayerController player)
    {
        float brightness = _plugin.Service.GetPlayerBrightness(player);

        _panel.SetVariableFor(player, "br_title", Text("menu.brightness", "BRIGHTNESS"));
        _panel.SetVariableFor(player, "br_desc",
            Text("panel.brightness.desc", "How bright your sky is. Everyone else keeps their own setting."));
        _panel.SetVariableFor(player, "br_value", brightness.ToString("F1"));
        _panel.SetVariableFor(player, "br_reset_lbl", Text("panel.reset", "RESET"));
    }

    private void RenderTint(CCSPlayerController player)
    {
        Color colour = _plugin.Service.GetPlayerColor(player);
        int argb = colour.ToArgb();

        // int.MaxValue is SkyData's no-tint sentinel, and it is NOT a colour anyone picked - it
        // happens to decode as half-transparent white. Nothing is marked selected in that state.
        bool none = argb == int.MaxValue;

        _panel.SetVariableFor(player, "tc_title", Text("menu.tintcolor", "TINT COLOR"));
        _panel.SetVariableFor(player, "tc_desc",
            Text("panel.tint.desc", "Tints your sky. WHITE is the neutral one - there is no separate clear."));

        string? match = null;

        foreach ((string id, KnownColor known) in Swatches)
        {
            bool selected = !none && Color.FromKnownColor(known).ToArgb() == argb;

            if (selected) match = known.ToString();

            _panel.SetClassFor(player, id, "selected", selected);
        }

        _panel.SetVariableFor(player, "tc_value",
            none ? Text("panel.tint.none", "NONE") : match ?? $"#{argb & 0xFFFFFF:X6}");
    }

    /// <summary>
    /// The three tab buttons and the underline under them.
    ///
    /// <para><b>The tabs are not registered in PanelHandle.Tabs on purpose.</b> A registered tab is
    /// dispatched as PanelAction.Tab and makes the library run its own Render first, which rewrites
    /// menu_page and has-pages from an item list this panel does not use. Unregistered, they arrive
    /// as plain buttons and the active state is ours to toggle - which is what MusicPanel does.</para>
    /// </summary>
    private void RenderTabs(CCSPlayerController player, Session s)
    {
        _panel.SetVariableFor(player, "tab_skybox_lbl", Text("panel.tab.skybox", "SKYBOX"));
        _panel.SetVariableFor(player, "tab_brightness_lbl", Text("panel.tab.brightness", "BRIGHTNESS"));
        _panel.SetVariableFor(player, "tab_tint_lbl", Text("panel.tab.tint", "TINT"));

        _panel.SetClassFor(player, "tab_skybox", "active", s.Tab == Tab.Skybox);
        _panel.SetClassFor(player, "tab_brightness", "active", s.Tab == Tab.Brightness);
        _panel.SetClassFor(player, "tab_tint", "active", s.Tab == Tab.Tint);

        _panel.SetClassFor(player, "view_skybox", "hidden", s.Tab != Tab.Skybox);
        _panel.SetClassFor(player, "view_brightness", "hidden", s.Tab != Tab.Brightness);
        _panel.SetClassFor(player, "view_tint", "hidden", s.Tab != Tab.Tint);

        // Added BEFORE the others are removed. Taking the current class off first leaves the bar
        // with no transform at all for that instant - it snaps to the origin and the slide is lost.
        int index = (int)s.Tab;

        _panel.SetClassFor(player, "tab_bar", $"at-{index}", true);

        for (int i = 0; i < 3; i++)
        {
            if (i != index) _panel.SetClassFor(player, "tab_bar", $"at-{i}", false);
        }
    }

    // ------------------------------------------------------------------ events

    private void OnEvent(PanelEvent e)
    {
        CCSPlayerController player = e.Player;
        if (player is not { IsValid: true }) return;

        if (e.Action == PanelAction.Close)
        {
            _sessions.Remove(player.SteamID);
            return;
        }

        if (!_sessions.TryGetValue(player.SteamID, out Session? s)) return;

        // The layout entity is bulk-deleted on a round restart. The library restores its own rows
        // and title, but never the per-player variables and classes this panel is made of - it
        // never saw what they meant. Without this the card comes back structurally intact and
        // completely blank.
        if (e.Action == PanelAction.Restored)
        {
            TryRender(player, s);
            return;
        }

        // nav_prev / nav_next arrive as Page because they are the contract's pager ids. The library
        // has already moved its own page counter, which is meaningless here - it paginates an item
        // list this panel does not use - so the direction is taken from the element id and applied
        // to ours. Paging mutates nothing, so it is exempt from the spectator gate.
        if (e.Action == PanelAction.Page)
        {
            int pageCount = Math.Max(1, (int)Math.Ceiling(Visible(player).Count / (double)Cells));
            int delta = e.ElementId == "nav_next" ? 1 : -1;

            s.Page = (s.Page + delta + pageCount) % pageCount;
            TryRender(player, s);
            return;
        }

        if (e.Action != PanelAction.Button) return;

        // Before anything else a button can do. Every branch below either applies a setting or opens
        // one of the views the chat menu gated on this same permission.
        if (!Authorised(player))
        {
            player.PrintToChat($"{_plugin.Localizer["prefix"]} {_plugin.Localizer["no.permission"]}");
            _panel.Close(player);
            return;
        }

        if (Handle(player, s, e.ElementId))
            TryRender(player, s);
    }

    /// <summary>Returns true when the card should be redrawn.</summary>
    private bool Handle(CCSPlayerController player, Session s, string id)
    {
        if (TabFor(id) is { } tab)
        {
            // Switching tab is what the chat menu called opening a submenu, and all three submenus
            // refused a spectator outright - so the refusal lives here rather than only on the
            // settings themselves.
            if (Blocked(player)) return false;

            if (s.Tab == tab) return false;   // clicking the tab you are on is not a redraw

            s.Tab = tab;
            return true;
        }

        if (CellIndex(id) is { } cell)
        {
            if (Blocked(player)) return false;

            if (cell >= s.CellKeys.Count) return false;

            string key = s.CellKeys[cell];

            // Re-checked against the config as it is now, resolved through the session's own map.
            // The client can only name a position on the page it was drawn; whether that position
            // still exists, and is still allowed, is decided here.
            if (!_plugin.Config.Skyboxs.TryGetValue(key, out Skybox? skybox)) return true;
            if (!Allowed(player, key, skybox)) return true;

            // SetSkybox returns false for a spectator AND for a genuine apply failure, which is why
            // the gate above is separate - otherwise a spectator would be told the skybox failed.
            bool applied = _plugin.Service.SetSkybox(player, key);

            player.PrintToChat(applied
                ? $"{_plugin.Localizer["prefix"]} {_plugin.Localizer["change.success"]}"
                : $"{_plugin.Localizer["prefix"]} {_plugin.Localizer["change.failed"]}");

            return true;
        }

        if (BrightnessStep(id) is { } step)
        {
            if (Blocked(player)) return false;

            // Read fresh rather than carried from the last render: the same clamps the chat menu
            // uses, against the value as it stands now, so holding a step cannot drift past them.
            float current = _plugin.Service.GetPlayerBrightness(player);

            float wanted = step switch
            {
                -5 => Math.Max(0.0f, current - 0.5f),
                -1 => Math.Max(0.0f, current - 0.1f),
                +1 => Math.Min(10.0f, current + 0.1f),
                +5 => Math.Min(10.0f, current + 0.5f),
                _ => 1.0f,                                  // br_reset - the SkyData default
            };

            _plugin.Service.SetBrightness(player, wanted);
            return true;
        }

        foreach ((string swatch, KnownColor known) in Swatches)
        {
            if (swatch != id) continue;

            if (Blocked(player)) return false;

            _plugin.Service.SetTintColor(player, Color.FromKnownColor(known));
            return true;
        }

        return false;
    }

    private static Tab? TabFor(string id) => id switch
    {
        "tab_skybox" => Tab.Skybox,
        "tab_brightness" => Tab.Brightness,
        "tab_tint" => Tab.Tint,
        _ => null,
    };

    /// <summary>Index of <c>sb0</c>..<c>sb17</c>, or null for anything else.</summary>
    private static int? CellIndex(string id)
    {
        if (!id.StartsWith("sb", StringComparison.Ordinal)) return null;

        return int.TryParse(id.AsSpan(2), out int index) && index >= 0 && index < Cells
            ? index
            : null;
    }

    /// <summary>Tenths of a step for the brightness buttons; 0 is the reset. Null for anything
    /// else.</summary>
    private static int? BrightnessStep(string id) => id switch
    {
        "br_down2" => -5,
        "br_down1" => -1,
        "br_up1" => +1,
        "br_up2" => +5,
        "br_reset" => 0,
        _ => null,
    };

    /// <summary>
    /// A localised string with a literal fallback.
    ///
    /// <para>The panel needs words the chat menus never had, and only en.json ships. A server
    /// running another language file would otherwise render the resource key itself -
    /// "panel.tab.tint" on a tab - which looks like a broken layout rather than a missing
    /// translation.</para>
    /// </summary>
    private string Text(string key, string fallback)
    {
        try
        {
            var localized = _plugin.Localizer[key];

            return localized.ResourceNotFound || string.IsNullOrWhiteSpace(localized.Value)
                ? fallback
                : localized.Value;
        }
        catch
        {
            return fallback;
        }
    }
}
