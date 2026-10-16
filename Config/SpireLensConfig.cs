using BaseLib.Config;
using Godot;

namespace SpireLens.Config;

/// <summary>
/// BaseLib persistence for SpireLens settings. Every setting is edited in the
/// in-game SpireLens menu (Core/SpireLensOptionsMenu.cs, opened with a Left
/// Shift tap or R3), which writes through Loader/RuntimeOptionsBridge.cs.
///
/// The property names are legacy on-disk keys: keep them stable. Every
/// setting is [ConfigHideInUI], which BaseLib still loads and saves normally
/// but generates no Mods-page row for; the Mods → SpireLens page only points
/// the player at the menu and shows build info.
/// </summary>
public sealed class SpireLensConfig : SimpleModConfig
{
    [ConfigHideInUI]
    public static bool ViewStatsToggleEnabled { get; set; }

    [ConfigHideInUI]
    public static bool ShowRemovedCardsInDeckView { get; set; } = true;

    [ConfigHideInUI]
    public static bool ShowAllMetaCardsInNotInDeckView { get; set; }

    [ConfigHideInUI]
    public static bool ShowEnemyStatsOnHover { get; set; }

    [ConfigHideInUI]
    public static bool ShowCardStatsDuringCombat { get; set; }

    [ConfigHideInUI]
    public static bool HideNonCombatRelicStats { get; set; }

    [ConfigHideInUI]
    public static bool ShowCombatOnlyRelicsAtCombatScreen { get; set; }

    [ConfigHideInUI]
    public static bool ShowHandTooltips { get; set; } = true;

    [ConfigHideInUI]
    public static bool UseVerboseHandStats { get; set; }

    [ConfigHideInUI]
    public static bool DisableCardStatsDuringCombat { get; set; }

    [ConfigHideInUI]
    public static bool EnableDebugLogging { get; set; }

    [ConfigHideInUI]
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

    [ConfigHideInUI]
    public static bool LegacyPrefsMigrated { get; set; }

    /// <summary>
    /// With every property hidden, BaseLib would drop SpireLens from the Mods
    /// list entirely. Keep the page so players looking there find the pointer.
    /// </summary>
    public override bool VisibleInModList() => true;

    public override void SetupConfigUI(Control root)
    {
        // Deliberately not base.SetupConfigUI: there are no generated rows to
        // show, and its "Restore defaults" button would reset settings behind
        // the back of the running Core, which caches them until it reloads.
        var pointer = CreateRawLabelControl(
            "SpireLens settings live in the SpireLens menu: tap Left Shift on keyboard "
            + "or press R3 on controller, from anywhere in the game.",
            22);
        // Same wrap-and-grow setup BaseLib uses for its own multi-line notice.
        pointer.FitContent = true;
        pointer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        root.AddChild(pointer);

        root.AddChild(CreateSectionHeader("Build Info", false));
        root.AddChild(CreateRawLabelControl($"Build version: {BuildVersion}", 18));
        root.AddChild(CreateRawLabelControl($"Build source: {BuildSource}", 18));
        root.AddChild(CreateRawLabelControl($"Commit: {BuildCommit}", 18));
        root.AddChild(CreateRawLabelControl($"Build date: {BuildDate}", 18));
        root.AddChild(CreateRawLabelControl($"Build timestamp UTC: {BuildTimestampUtc}", 18));
    }
}
