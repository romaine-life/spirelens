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

    public static void Initialize()
    {
        MigrateLegacyPrefsIfNeeded();
    }

    public static string GetCurrentOptionsJson()
    {
        return JsonSerializer.Serialize(CreateSnapshot(), JsonOptions);
    }

    public static void SetViewStatsToggleEnabled(bool isEnabled)
    {
        if (SpireLensConfig.ViewStatsToggleEnabled == isEnabled) return;

        SpireLensConfig.ViewStatsToggleEnabled = isEnabled;
        ModConfig.SaveDebounced<SpireLensConfig>();
    }

    public static void SetShowRemovedCardsInDeckView(bool isEnabled)
    {
        if (SpireLensConfig.ShowRemovedCardsInDeckView == isEnabled) return;

        SpireLensConfig.ShowRemovedCardsInDeckView = isEnabled;
        ModConfig.SaveDebounced<SpireLensConfig>();
    }

    public static void SetShowAllMetaCardsInNotInDeckView(bool isEnabled)
    {
        if (SpireLensConfig.ShowAllMetaCardsInNotInDeckView == isEnabled) return;

        SpireLensConfig.ShowAllMetaCardsInNotInDeckView = isEnabled;
        ModConfig.SaveDebounced<SpireLensConfig>();
    }

    public static void SetShowEnemyStatsOnHover(bool isEnabled)
    {
        if (SpireLensConfig.ShowEnemyStatsOnHover == isEnabled) return;

        SpireLensConfig.ShowEnemyStatsOnHover = isEnabled;
        ModConfig.SaveDebounced<SpireLensConfig>();
    }

    public static void SetShowCardStatsDuringCombat(bool isEnabled)
    {
        if (SpireLensConfig.ShowCardStatsDuringCombat == isEnabled) return;

        SpireLensConfig.ShowCardStatsDuringCombat = isEnabled;
        ModConfig.SaveDebounced<SpireLensConfig>();
    }

    public static void SetHideNonCombatRelicStats(bool isEnabled)
    {
        if (SpireLensConfig.HideNonCombatRelicStats == isEnabled
            && (!isEnabled || !SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen)) return;

        SpireLensConfig.HideNonCombatRelicStats = isEnabled;
        if (isEnabled)
            SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen = false;
        ModConfig.SaveDebounced<SpireLensConfig>();
    }

    public static void SetShowCombatOnlyRelicsAtCombatScreen(bool isEnabled)
    {
        if (SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen == isEnabled
            && (!isEnabled || !SpireLensConfig.HideNonCombatRelicStats)) return;

        SpireLensConfig.ShowCombatOnlyRelicsAtCombatScreen = isEnabled;
        if (isEnabled)
            SpireLensConfig.HideNonCombatRelicStats = false;
        ModConfig.SaveDebounced<SpireLensConfig>();
    }

    public static void SetVerboseHandStatsEnabled(bool isEnabled)
    {
        if (SpireLensConfig.UseVerboseHandStats == isEnabled) return;

        SpireLensConfig.UseVerboseHandStats = isEnabled;
        ModConfig.SaveDebounced<SpireLensConfig>();
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
        ModConfig.SaveDebounced<SpireLensConfig>();
    }

    private static bool TryApplyBuildTimeZone(string value)
    {
        // Exact member names only: Enum.TryParse would also accept numbers
        // and comma lists, which are never values the menu sends.
        if (Array.IndexOf(Enum.GetNames<BuildDisplayTimeZone>(), value) < 0) return false;

        var zone = Enum.Parse<BuildDisplayTimeZone>(value);
        if (SpireLensConfig.BuildTimeZone == zone) return true;

        SpireLensConfig.BuildTimeZone = zone;
        ModConfig.SaveDebounced<SpireLensConfig>();
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
        ModConfig.SaveDebounced<SpireLensConfig>();
    }

    private sealed class LegacyPrefs
    {
        [JsonPropertyName("view_stats_ticked")]
        public bool ViewStatsTicked { get; set; }
    }
}
