using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Nodes.TopBar;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// Which mouse clicks a replay lets through to the game's own view-only
/// screens (docs/replay-player.md, "Looking around during a replay"). The
/// replay swallows clicks so the watcher can't act on the replayed run;
/// looking at the deck, the combat piles, the pause menu, settings and the
/// compendium changes nothing, so their buttons and the screens they open
/// take clicks. The controls on those screens that would end the replayed
/// run behind SpireLens's back (the pause menu's Give Up, Save &amp; Quit and
/// Disconnect, settings' Abandon Run) stop the replay instead, the way Esc
/// does, so the user's data is restored.
///
/// While a screen the watcher opened is up, playback holds, so the replay
/// doesn't play cards or pick rewards underneath it.
/// </summary>
internal static class ReplayOverlayAccess
{
    private static bool _watcherOpenedScreen;

    /// <summary>The control under the mouse, or null.</summary>
    public static Control? Hovered()
    {
        try { return NGame.Instance?.GetViewport()?.GuiGetHoveredControl(); }
        catch { return null; }
    }

    /// <summary>A control that ends the run: clicking it stops the replay instead.</summary>
    public static bool EndsRun(Control? hovered)
    {
        for (Node? node = hovered; node != null; node = node.GetParent())
        {
            if (node is NAbandonRunButton) return true;
            if (node is NPauseMenuButton button)
            {
                var menu = FindAncestor<NPauseMenu>(button);
                if (menu != null && (button == menu._giveUpButton || button == menu._saveAndQuitButton
                                                                  || button == menu._disconnectButton))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A control that only shows things: the top bar's deck and pause (gear)
    /// buttons, the combat pile buttons, or anything on a screen or popup
    /// they opened. A click on one of the opening buttons marks the screen as
    /// the watcher's, which holds playback until it closes.
    /// </summary>
    public static bool IsViewOnly(Control? hovered)
    {
        if (hovered == null) return false;
        for (Node? node = hovered; node != null; node = node.GetParent())
        {
            if (node is NTopBarDeckButton or NTopBarPauseButton or NDrawPileButton or NDiscardPileButton or NExhaustPileButton)
            {
                _watcherOpenedScreen = true;
                return true;
            }
        }
        var capstones = NCapstoneContainer.Instance;
        if (capstones != null && GodotObject.IsInstanceValid(capstones) && capstones.InUse && capstones.IsAncestorOf(hovered))
            return true;
        var modals = NModalContainer.Instance;
        return modals != null && GodotObject.IsInstanceValid(modals) && modals.IsAncestorOf(hovered);
    }

    /// <summary>
    /// A screen the watcher opened is still up: playback holds, and Esc goes
    /// to the game to close it rather than stopping the replay.
    /// </summary>
    public static bool WatcherScreenOpen
    {
        get
        {
            if (!_watcherOpenedScreen) return false;
            var capstones = NCapstoneContainer.Instance;
            if (capstones != null && GodotObject.IsInstanceValid(capstones) && capstones.InUse) return true;
            _watcherOpenedScreen = false;
            return false;
        }
    }

    /// <summary>A new replay or a reload starts with no screen of the watcher's open.</summary>
    public static void Reset() => _watcherOpenedScreen = false;

    private static T? FindAncestor<T>(Node node) where T : Node
    {
        for (Node? n = node; n != null; n = n.GetParent())
            if (n is T match) return match;
        return null;
    }
}
