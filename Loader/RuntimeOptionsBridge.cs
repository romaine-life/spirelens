using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaseLib.Config;
using SpireLens.Config;
using Godot;

namespace SpireLens.Loader;

public sealed class RuntimeOptionsSnapshot
{
    public bool ViewStatsToggleEnabled { get; set; }
    public bool ShowRemovedCardsInDeckView { get; set; } = true;
    public bool ShowAllMetaCardsInNotInDeckView { get; set; }
    public bool ShowEnemyStatsOnHover { get; set; }
    public bool ShowCardStatsDuringCombat { get; set; }
    public bool HideNonCombatRelicStats { get; set; }
    public bool ShowCombatOnlyRelicsAtCombatScreen { get; set; }
    public bool ShowHandTooltips { get; set; } = true;
    public bool UseVerboseHandStats { get; set; }
    public bool DisableCardStatsDuringCombat { get; set; }
    public bool EnableDebugLogging { get; set; }
    public string BuildTimeZoneId { get; set; } = "America/Los_Angeles";

    // Additive fields for the Core-hosted SpireLens menu. A Core reading an
    // older Loader's snapshot sees them missing and degrades on its own.
    public string BuildTimeZone { get; set; } = nameof(BuildDisplayTimeZone.Pacific);
    public string BuildVersion { get; set; } = "";
    public string BuildSource { get; set; } = "";
    public string BuildCommit { get; set; } = "";
    public string BuildDate { get; set; } = "";
    public string BuildTimestampUtc { get; set; } = "";
}

public static class RuntimeOptionsBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly JsonSerializerOptions LegacyPrefsOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Every user-facing setting the Core may write through
    /// <see cref="SetOption"/>, keyed by its BaseLib property name (the legacy
    /// on-disk key). Anything not listed — e.g. LegacyPrefsMigrated — is
    /// rejected, so the hot-reloaded Core can never write arbitrary config.
    /// </summary>
    private static readonly Dictionary<string, Func<string, bool>> OptionSetters =
        new(StringComparer.Ordinal)
        {
            [nameof(SpireLensConfig.ViewStatsToggleEnabled)] =
                value => TryApplyBool(value, SetViewStatsToggleEnabled),
            [nameof(SpireLensConfig.ShowRemovedCardsInDeckView)] =
                value => TryApplyBool(value, SetShowRemovedCardsInDeckView),
            [nameof(SpireLensConfig.ShowAllMetaCardsInNotInDeckView)] =
                value => TryApplyBool(value, SetShowAllMetaCardsInNotInDeckView),
            [nameof(SpireLensConfig.ShowEnemyStatsOnHover)] =
                value => TryApplyBool(value, SetShowEnemyStatsOnHover),
            [nameof(SpireLensConfig.ShowCardStatsDuringCombat)] =
                value => TryApplyBool(value, SetShowCardStatsDuringCombat),
            [nameof(SpireLensConfig.HideNonCombatRelicStats)] =
                value => TryApplyBool(value, SetHideNonCombatRelicStats),
            [nameof(SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen)] =
                value => TryApplyBool(value, SetShowCombatOnlyRelicsAtCombatScreen),
            [nameof(SpireLensConfig.ShowHandTooltips)] =
                value => TryApplyBool(value, enabled => SaveIfChanged(
                    SpireLensConfig.ShowHandTooltips,
                    enabled,
                    v => SpireLensConfig.ShowHandTooltips = v)),
            [nameof(SpireLensConfig.UseVerboseHandStats)] =
                value => TryApplyBool(value, SetVerboseHandStatsEnabled),
            [nameof(SpireLensConfig.DisableCardStatsDuringCombat)] =
                value => TryApplyBool(value, enabled => SaveIfChanged(
                    SpireLensConfig.DisableCardStatsDuringCombat,
                    enabled,
                    v => SpireLensConfig.DisableCardStatsDuringCombat = v)),
            [nameof(SpireLensConfig.EnableDebugLogging)] =
                value => TryApplyBool(value, enabled => SaveIfChanged(
                    SpireLensConfig.EnableDebugLogging,
                    enabled,
                    v => SpireLensConfig.EnableDebugLogging = v)),
            [nameof(SpireLensConfig.BuildTimeZone)] = TryApplyBuildTimeZone,
        };

    private static Action? _coreOptionsChanged;
    private static Action<Control, Func<string, Control>, Func<string, Control>, Func<string, string, Action?, Control>>?
        _coreSettingsSection;
    private static int _coreHooksGeneration;
    private static bool _lastHideNonCombatRelicStats;
    private static bool _lastShowCombatOnlyRelicsAtCombatScreen;

    public static void Initialize()
    {
        MigrateLegacyPrefsIfNeeded();
        RememberRelicFilterState();
    }

    /// <summary>
    /// Called by the Core (by reflection) on every Initialize, and with nulls
    /// on Shutdown, so a hot-reloaded Core replaces the previous one's hooks
    /// and an orphaned Core is never called back. <paramref name="optionsChanged"/>
    /// runs after the Mods page changes a setting, so the Core drops any state
    /// it cached from the config; <paramref name="settingsSection"/> renders
    /// the Core's part of that page. The signature is a cross-assembly
    /// contract: keep it stable.
    ///
    /// settingsSection receives the host container plus three Loader-made
    /// factories, so the Core's rows look and navigate exactly like BaseLib's
    /// own: makeSection(title) adds a collapsible section to the host and
    /// returns its content container, makeNote(text) returns a quiet label,
    /// and makeRow(label, buttonText, onPressed) returns an option row with a
    /// button — or, when onPressed is null, the button text as a greyed
    /// label. Only BCL and Godot types cross the boundary, because the Core
    /// does not reference BaseLib or the Loader.
    /// </summary>
    public static void RegisterCoreHooks(
        Action? optionsChanged,
        Action<Control, Func<string, Control>, Func<string, Control>, Func<string, string, Action?, Control>>? settingsSection)
    {
        _coreOptionsChanged = optionsChanged;
        _coreSettingsSection = settingsSection;
        _coreHooksGeneration++;
    }

    internal static bool HasCoreSettingsSection => _coreSettingsSection != null;

    internal static int CoreHooksGeneration => _coreHooksGeneration;

    /// <summary>Lets the registered Core fill its part of the Mods page.</summary>
    internal static bool TryFillCoreSettingsSection(
        Control host,
        Func<string, Control> makeSection,
        Func<string, Control> makeNote,
        Func<string, string, Action?, Control> makeRow)
    {
        var fill = _coreSettingsSection;
        if (fill == null) return false;

        try
        {
            fill(host, makeSection, makeNote, makeRow);
            return true;
        }
        catch (Exception e)
        {
            LoaderMain.Logger.Error($"RuntimeOptionsBridge: Core settings section failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// A Mods-page control changed the config (or Restore Defaults ran).
    /// Keep the relic-bar filter pair exclusive, as the Core's own setters do,
    /// then tell the Core to re-read everything it caches.
    /// </summary>
    internal static void OnSettingsPageChanged()
    {
        EnforceRelicFilterExclusivity();
        NotifyCoreOptionsChanged();
    }

    private static void NotifyCoreOptionsChanged()
    {
        try
        {
            _coreOptionsChanged?.Invoke();
        }
        catch (Exception e)
        {
            LoaderMain.Logger.Error($"RuntimeOptionsBridge: Core options-changed hook failed: {e}");
        }
    }

    /// <summary>
    /// The filter the player ticked last wins: whichever was already on before
    /// this change is the one turned off.
    /// </summary>
    private static void EnforceRelicFilterExclusivity()
    {
        if (SpireLensConfig.HideNonCombatRelicStats && SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen)
        {
            if (_lastHideNonCombatRelicStats)
                SpireLensConfig.HideNonCombatRelicStats = false;
            else
                SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen = false;

            ModConfig.SaveDebounced<SpireLensConfig>();
            RefreshOpenSettingsPage();
        }

        RememberRelicFilterState();
    }

    private static void RememberRelicFilterState()
    {
        _lastHideNonCombatRelicStats = SpireLensConfig.HideNonCombatRelicStats;
        _lastShowCombatOnlyRelicsAtCombatScreen = SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen;
    }

    /// <summary>
    /// Saves a Core-originated change and redraws an open Mods page's
    /// controls from the config, since the SpireLens overlay can be opened on
    /// top of that page.
    /// </summary>
    private static void SaveFromCore()
    {
        ModConfig.SaveDebounced<SpireLensConfig>();
        RememberRelicFilterState();
        RefreshOpenSettingsPage();
    }

    private static void RefreshOpenSettingsPage()
    {
        try
        {
            // OnConfigReloaded is what BaseLib's own tickboxes and dropdowns
            // listen to in order to re-read their property.
            ModConfigRegistry.Get<SpireLensConfig>()?.ConfigReloaded();
        }
        catch (Exception e)
        {
            LoaderMain.Logger.Warn($"RuntimeOptionsBridge: refreshing the Mods page failed: {e.Message}");
        }
    }

    public static string GetCurrentOptionsJson()
    {
        return JsonSerializer.Serialize(CreateSnapshot(), JsonOptions);
    }

    public static void SetViewStatsToggleEnabled(bool isEnabled)
    {
        if (SpireLensConfig.ViewStatsToggleEnabled == isEnabled) return;

        SpireLensConfig.ViewStatsToggleEnabled = isEnabled;
        SaveFromCore();
    }

    public static void SetShowRemovedCardsInDeckView(bool isEnabled)
    {
        if (SpireLensConfig.ShowRemovedCardsInDeckView == isEnabled) return;

        SpireLensConfig.ShowRemovedCardsInDeckView = isEnabled;
        SaveFromCore();
    }

    public static void SetShowAllMetaCardsInNotInDeckView(bool isEnabled)
    {
        if (SpireLensConfig.ShowAllMetaCardsInNotInDeckView == isEnabled) return;

        SpireLensConfig.ShowAllMetaCardsInNotInDeckView = isEnabled;
        SaveFromCore();
    }

    public static void SetShowEnemyStatsOnHover(bool isEnabled)
    {
        if (SpireLensConfig.ShowEnemyStatsOnHover == isEnabled) return;

        SpireLensConfig.ShowEnemyStatsOnHover = isEnabled;
        SaveFromCore();
    }

    public static void SetShowCardStatsDuringCombat(bool isEnabled)
    {
        if (SpireLensConfig.ShowCardStatsDuringCombat == isEnabled) return;

        SpireLensConfig.ShowCardStatsDuringCombat = isEnabled;
        SaveFromCore();
    }

    public static void SetHideNonCombatRelicStats(bool isEnabled)
    {
        if (SpireLensConfig.HideNonCombatRelicStats == isEnabled
            && (!isEnabled || !SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen)) return;

        SpireLensConfig.HideNonCombatRelicStats = isEnabled;
        if (isEnabled)
            SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen = false;
        SaveFromCore();
    }

    public static void SetShowCombatOnlyRelicsAtCombatScreen(bool isEnabled)
    {
        if (SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen == isEnabled
            && (!isEnabled || !SpireLensConfig.HideNonCombatRelicStats)) return;

        SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen = isEnabled;
        if (isEnabled)
            SpireLensConfig.HideNonCombatRelicStats = false;
        SaveFromCore();
    }

    public static void SetVerboseHandStatsEnabled(bool isEnabled)
    {
        if (SpireLensConfig.UseVerboseHandStats == isEnabled) return;

        SpireLensConfig.UseVerboseHandStats = isEnabled;
        SaveFromCore();
    }

    /// <summary>
    /// Generic, validated setter for the Core-hosted SpireLens menu. The Core
    /// calls it by reflection, so its name and (string, string) signature are
    /// a cross-assembly contract: keep them stable. <paramref name="name"/> is
    /// the BaseLib property name; <paramref name="value"/> is "true"/"false"
    /// for toggles or the enum member name for <c>BuildTimeZone</c>. Returns
    /// false (and changes nothing) for an unknown name or an invalid value.
    /// The per-option setters above stay for Cores built before this method.
    /// </summary>
    public static bool SetOption(string name, string value)
    {
        if (string.IsNullOrEmpty(name) || value == null
            || !OptionSetters.TryGetValue(name, out var apply))
        {
            LoaderMain.Logger.Warn($"RuntimeOptionsBridge.SetOption rejected unknown option '{name}'");
            return false;
        }

        if (apply(value)) return true;

        LoaderMain.Logger.Warn($"RuntimeOptionsBridge.SetOption rejected value '{value}' for {name}");
        return false;
    }

    private static bool TryApplyBool(string value, Action<bool> apply)
    {
        if (!bool.TryParse(value, out var enabled)) return false;
        apply(enabled);
        return true;
    }

    private static void SaveIfChanged(bool current, bool next, Action<bool> assign)
    {
        if (current == next) return;

        assign(next);
        SaveFromCore();
    }

    private static bool TryApplyBuildTimeZone(string value)
    {
        // Exact member names only: Enum.TryParse would also accept numbers
        // and comma lists, which are never values the menu sends.
        if (Array.IndexOf(Enum.GetNames<BuildDisplayTimeZone>(), value) < 0) return false;

        var zone = Enum.Parse<BuildDisplayTimeZone>(value);
        if (SpireLensConfig.BuildTimeZone == zone) return true;

        SpireLensConfig.BuildTimeZone = zone;
        SaveFromCore();
        return true;
    }

    private static RuntimeOptionsSnapshot CreateSnapshot()
    {
        return new RuntimeOptionsSnapshot
        {
            ViewStatsToggleEnabled = SpireLensConfig.ViewStatsToggleEnabled,
            ShowRemovedCardsInDeckView = SpireLensConfig.ShowRemovedCardsInDeckView,
            ShowAllMetaCardsInNotInDeckView =
                SpireLensConfig.ShowAllMetaCardsInNotInDeckView,
            ShowEnemyStatsOnHover = SpireLensConfig.ShowEnemyStatsOnHover,
            ShowCardStatsDuringCombat = SpireLensConfig.ShowCardStatsDuringCombat,
            HideNonCombatRelicStats = SpireLensConfig.HideNonCombatRelicStats,
            ShowCombatOnlyRelicsAtCombatScreen = SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen,
            ShowHandTooltips = SpireLensConfig.ShowHandTooltips,
            UseVerboseHandStats = SpireLensConfig.UseVerboseHandStats,
            DisableCardStatsDuringCombat = SpireLensConfig.DisableCardStatsDuringCombat,
            EnableDebugLogging = SpireLensConfig.EnableDebugLogging,
            BuildTimeZoneId = BuildMetadataFormatter.GetTimeZoneId(SpireLensConfig.BuildTimeZone),
            BuildTimeZone = SpireLensConfig.BuildTimeZone.ToString(),
            BuildVersion = SpireLensConfig.BuildVersion,
            BuildSource = SpireLensConfig.BuildSource,
            BuildCommit = SpireLensConfig.BuildCommit,
            BuildDate = SpireLensConfig.BuildDate,
            BuildTimestampUtc = SpireLensConfig.BuildTimestampUtc,
        };
    }

    private static void MigrateLegacyPrefsIfNeeded()
    {
        if (SpireLensConfig.LegacyPrefsMigrated) return;

        try
        {
            var legacyPath = ProjectSettings.GlobalizePath("user://SpireLens/prefs.json");
            if (File.Exists(legacyPath))
            {
                var json = File.ReadAllText(legacyPath);
                var legacyPrefs = JsonSerializer.Deserialize<LegacyPrefs>(json, LegacyPrefsOptions);
                if (legacyPrefs != null)
                {
                    SpireLensConfig.ViewStatsToggleEnabled = legacyPrefs.ViewStatsTicked;
                }
            }
        }
        catch (Exception e)
        {
            LoaderMain.Logger.Error($"RuntimeOptionsBridge migration failed: {e}");
        }

        SpireLensConfig.LegacyPrefsMigrated = true;
        SaveFromCore();
    }

    private sealed class LegacyPrefs
    {
        [JsonPropertyName("view_stats_ticked")]
        public bool ViewStatsTicked { get; set; }
    }
}
