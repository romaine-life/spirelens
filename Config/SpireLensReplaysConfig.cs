using System;
using System.Collections.Generic;
using System.IO;
using BaseLib.Config;
using BaseLib.Config.UI;
using BaseLib.Extensions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using SpireLens.Loader;

// BaseLib derives a config's mod-list title key, row-key prefix and .cfg file
// name from the root namespace, so this page needs a root of its own to be a
// separate entry. Under SpireLens it would also save its empty property set
// over SpireLens.cfg.
namespace SpireLensReplays;

/// <summary>
/// The game's Mod Configuration → SpireLens Replays page: playlists and the
/// recorded-run list. It has no settings of its own; the hot-reloaded Core
/// renders the whole page through <see cref="RuntimeOptionsBridge.RegisterCoreHooks"/>
/// every time it opens, so the lists are current and always this Core's code.
/// </summary>
public sealed class SpireLensReplaysConfig : SimpleModConfig
{
    public const string RegistryId = "SpireLensReplays";

    private const string Title = "SpireLens Replays";

    /// <summary>The settings_ui key BaseLib reads this entry's mod-list title from.</summary>
    private static readonly string TitleKey =
        typeof(SpireLensReplaysConfig).GetPrefix().TrimEnd(TypePrefix.PrefixSplitChar) + ".mod_title";

    public override bool VisibleInModList() => true;

    public override void SetupConfigUI(Control root)
    {
        int generation = RuntimeOptionsBridge.CoreHooksGeneration;
        bool filled = RuntimeOptionsBridge.TryFillCoreSettingsSection(
            root,
            title => MakeSection(root, title),
            MakeNote,
            (label, buttonText, onPressed) => MakeRow(label, buttonText, onPressed, generation));

        if (!filled)
            root.AddChild(MakeNote("Replays and playlists appear here once the SpireLens Core has loaded."));

        SetupFocusNeighbors(root);
    }

    /// <summary>
    /// Mods load before the game builds its localization tables, and SpireLens
    /// ships no localization files, so the title is added to every
    /// settings_ui table as it loads, including on a language change.
    /// </summary>
    internal static void InstallTitle(Harmony harmony) =>
        harmony.Patch(
            AccessTools.Method(typeof(LocManager), nameof(LocManager.LoadTable)),
            postfix: new HarmonyMethod(typeof(SpireLensReplaysConfig), nameof(AddTitle)));

    private static void AddTitle(string path, Dictionary<string, string>? __result)
    {
        if (__result != null && Path.GetFileName(path) == "settings_ui.json")
            __result.TryAdd(TitleKey, Title);
    }

    private Control MakeSection(Control host, string title)
    {
        var section = CreateCollapsibleSection(title);
        host.AddChild(section);
        return section.ContentContainer;
    }

    private static Control MakeNote(string text)
    {
        var note = CreateRawLabelControl(text, 22);
        // Same wrap-and-grow setup BaseLib uses for its own multi-line notice.
        note.FitContent = true;
        note.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        note.Modulate = new Color(0.75f, 0.75f, 0.75f);
        return note;
    }

    /// <summary>
    /// An option row shaped like BaseLib's generated ones: label on the left,
    /// an <see cref="NConfigButton"/> on the right, so focus navigation and
    /// controller confirm behave the same. The press is dropped if the Core
    /// that built the row has since been hot-reloaded away.
    /// </summary>
    private Control MakeRow(string label, string buttonText, Action? onPressed, int generation)
    {
        var labelControl = CreateRawLabelControl(label, 24);
        Control setting;
        if (onPressed == null)
        {
            var text = CreateRawLabelControl(buttonText, 22);
            text.Modulate = new Color(0.55f, 0.55f, 0.55f);
            // NConfigOptionRow is a MarginContainer that overlays its two
            // children; pin this one to the right edge like a button.
            text.CustomMinimumSize = new Vector2(260, 64);
            text.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
            setting = text;
        }
        else
        {
            setting = CreateRawButtonControl(buttonText, () =>
            {
                if (generation != RuntimeOptionsBridge.CoreHooksGeneration)
                {
                    LoaderMain.Logger.Warn(
                        "SpireLens Replays page: ignored a press from before a Core reload; reopen the page");
                    return;
                }

                try
                {
                    onPressed();
                }
                catch (Exception e)
                {
                    LoaderMain.Logger.Error($"SpireLens Replays page: action '{buttonText}' failed: {e}");
                }
            });
        }

        return new NConfigOptionRow(ModPrefix, $"SpireLensCoreRow_{label.GetHashCode():X8}", labelControl, setting);
    }
}
