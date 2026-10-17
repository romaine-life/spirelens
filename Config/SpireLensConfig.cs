using System;
using BaseLib.Config;
using BaseLib.Config.UI;
using Godot;
using SpireLens.Loader;

namespace SpireLens.Config;

/// <summary>
/// BaseLib persistence and the game's Mods → SpireLens page, which is the home
/// for every SpireLens setting. The SpireLens overlay (Left Shift / R3) is an
/// optional in-game shortcut to the same settings; nothing requires it.
///
/// The property names are legacy on-disk keys: keep them stable. Declaration
/// order and [ConfigSection] only shape the page (BaseLib saves by name).
/// Below the generated rows the hot-reloaded Core renders its own section
/// (Replay) through <see cref="RuntimeOptionsBridge.RegisterCoreHooks"/>.
/// </summary>
public sealed class SpireLensConfig : SimpleModConfig
{
    [ConfigSection("Stats display")]
    public static bool ViewStatsToggleEnabled { get; set; }

    public static bool ShowCardStatsDuringCombat { get; set; }

    public static bool ShowEnemyStatsOnHover { get; set; }

    [ConfigSection("Deck view")]
    public static bool ShowRemovedCardsInDeckView { get; set; } = true;

    public static bool ShowAllMetaCardsInNotInDeckView { get; set; }

    [ConfigSection("Cards in hand")]
    public static bool ShowHandTooltips { get; set; } = true;

    public static bool UseVerboseHandStats { get; set; }

    [ConfigSection("Relic bar filter (choose one)")]
    public static bool ShowCombatOnlyRelicsAtCombatScreen { get; set; }

    public static bool HideNonCombatRelicStats { get; set; }

    [ConfigSection("Performance")]
    public static bool DisableCardStatsDuringCombat { get; set; }

    [ConfigSection("Diagnostics")]
    public static bool EnableDebugLogging { get; set; }

    [ConfigSection("Build")]
    public static BuildDisplayTimeZone BuildTimeZone { get; set; } = BuildDisplayTimeZone.Pacific;

    [ConfigIgnore]
    public static string BuildVersion => BuildInfo.Version;

    [ConfigIgnore]
    public static string BuildSource => BuildInfo.Source;

    [ConfigIgnore]
    public static string BuildCommit => BuildInfo.CommitHash;

    [ConfigIgnore]
    public static string BuildDate => BuildMetadataFormatter.FormatBuildDate(
        BuildInfo.BuildTimestampUtc,
        BuildTimeZone);

    [ConfigIgnore]
    public static string BuildTimestampUtc => BuildInfo.BuildTimestampUtc;

    // Bookkeeping, not a setting: Restore Defaults must not re-run the
    // one-time legacy prefs migration.
    [ConfigHideInUI]
    [ConfigIgnoreRestoreDefaults]
    public static bool LegacyPrefsMigrated { get; set; }

    /// <summary>Player-facing labels, keyed by the legacy property names.</summary>
    private static readonly (string Property, string Label)[] Labels =
    [
        (nameof(ViewStatsToggleEnabled), "SpireLens: on/off"),
        (nameof(ShowCardStatsDuringCombat), "SpireLens: card stats"),
        (nameof(ShowEnemyStatsOnHover), "Show monster stats"),
        (nameof(ShowRemovedCardsInDeckView), "Show cards not in deck"),
        (nameof(ShowAllMetaCardsInNotInDeckView), "Show all meta-cards in \"not in deck\" view"),
        (nameof(ShowHandTooltips), "Show card stats when hovering the hand"),
        (nameof(UseVerboseHandStats), "Full card stats in hand (instead of compact)"),
        (nameof(ShowCombatOnlyRelicsAtCombatScreen), "Show combat-only relics at the combat screen"),
        (nameof(HideNonCombatRelicStats), "Force show only combat relics on all screens"),
        (nameof(DisableCardStatsDuringCombat), "Pause card tracking during combat"),
        (nameof(EnableDebugLogging), "Debug logging"),
        (nameof(BuildTimeZone), "Build date time zone"),
    ];

    public SpireLensConfig()
    {
        // BaseLib's own controls call Changed() after writing a property. The
        // running Core caches some settings, so every page edit is forwarded.
        ConfigChanged += (_, _) => RuntimeOptionsBridge.OnSettingsPageChanged();
    }

    public override void SetupConfigUI(Control root)
    {
        // base.SetupConfigUI in pieces, so the Core section and build info
        // land before Restore Defaults and inside the focus chain.
        GenerateOptionsForAllProperties(root);
        foreach (var (property, label) in Labels)
            RelabelGeneratedOption(root, property, label);

        var buildSection = FindSectionOf(root, nameof(BuildTimeZone));
        AddCoreSection(root, buildSection);
        AddBuildInfo(root, nameof(BuildTimeZone));

        AddRestoreDefaultsButton(root);
        SetupFocusNeighbors(root);
    }

    /// <summary>
    /// Restore Defaults writes the properties directly, so the Core is told
    /// the same way as for any other page edit.
    /// </summary>
    protected override void RestoreDefaultsNoConfirm()
    {
        base.RestoreDefaultsNoConfirm();
        RuntimeOptionsBridge.OnSettingsPageChanged();
    }

    /// <summary>
    /// Hosts whatever the registered Core renders (rebuilt on every page open,
    /// so the replay list is current), placed above the Build section.
    /// </summary>
    private void AddCoreSection(Control root, Control? beforeSection)
    {
        var host = new VBoxContainer
        {
            Name = "SpireLensCoreSection",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        root.AddChild(host);
        if (beforeSection != null && beforeSection.GetParent() == root)
            root.MoveChild(host, beforeSection.GetIndex());

        int generation = RuntimeOptionsBridge.CoreHooksGeneration;
        bool filled = RuntimeOptionsBridge.TryFillCoreSettingsSection(
            host,
            title => MakeSection(host, title),
            MakeNote,
            (label, buttonText, onPressed) => MakeRow(label, buttonText, onPressed, generation));

        if (!filled)
        {
            host.AddChild(MakeNote(
                "Replay and other SpireLens actions appear here once the SpireLens Core has loaded."));
        }
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
                        "SpireLens Mods page: ignored a press from before a Core reload; reopen the page");
                    return;
                }

                try
                {
                    onPressed();
                }
                catch (Exception e)
                {
                    LoaderMain.Logger.Error($"SpireLens Mods page: action '{buttonText}' failed: {e}");
                }
            });
        }

        return new NConfigOptionRow(ModPrefix, $"SpireLensCoreRow_{label.GetHashCode():X8}", labelControl, setting);
    }

    private void AddBuildInfo(Control root, string buildRowName)
    {
        // Inside the generated Build section, under its time-zone dropdown.
        var buildRow = root.FindChild(buildRowName, recursive: true, owned: false);
        var target = buildRow?.GetParent() as Control ?? root;
        target.AddChild(CreateRawLabelControl($"Build version: {BuildVersion}", 18));
        target.AddChild(CreateRawLabelControl($"Build source: {BuildSource}", 18));
        target.AddChild(CreateRawLabelControl($"Commit: {BuildCommit}", 18));
        target.AddChild(CreateRawLabelControl($"Build date: {BuildDate}", 18));
        target.AddChild(CreateRawLabelControl($"Build timestamp UTC: {BuildTimestampUtc}", 18));
    }

    /// <summary>The top-level section node that holds a generated row.</summary>
    private static Control? FindSectionOf(Node root, string rowName)
    {
        Node? node = root.FindChild(rowName, recursive: true, owned: false);
        while (node != null && node.GetParent() != root)
            node = node.GetParent();
        return node as Control;
    }

    private static void RelabelGeneratedOption(Node root, string optionName, string labelText)
    {
        var row = root.FindChild(optionName, recursive: true, owned: false);
        if (row?.GetChildCount() > 0 && row.GetChild(0) is RichTextLabel label)
            label.Text = labelText;
    }
}
