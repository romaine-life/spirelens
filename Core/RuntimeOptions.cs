using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace SpireLens.Core;

public sealed class RuntimeOptions
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

    // Added with the generic bridge setter. An older Loader's snapshot omits
    // them, which leaves these null.
    public string? BuildTimeZone { get; set; }
    public string? BuildVersion { get; set; }
    public string? BuildSource { get; set; }
    public string? BuildCommit { get; set; }
    public string? BuildDate { get; set; }
    public string? BuildTimestampUtc { get; set; }

    /// <summary>
    /// The selected build-date zone as its Loader enum member name. Falls back
    /// to <see cref="BuildTimeZoneId"/> for a Loader whose snapshot predates
    /// <see cref="BuildTimeZone"/>.
    /// </summary>
    public string ResolveBuildTimeZoneName()
    {
        if (!string.IsNullOrEmpty(BuildTimeZone)) return BuildTimeZone;

        return BuildTimeZoneId switch
        {
            "America/Los_Angeles" => "Pacific",
            "America/New_York" => "Eastern",
            "America/Chicago" => "Central",
            "America/Denver" => "Mountain",
            "UTC" => "UTC",
            _ => "Local",
        };
    }
}

public static class RuntimeOptionsProvider
{
    private const string BridgeTypeName = "SpireLens.Loader.RuntimeOptionsBridge";
    private const string GetCurrentOptionsJsonMethodName = "GetCurrentOptionsJson";
    private const string SetViewStatsToggleEnabledMethodName = "SetViewStatsToggleEnabled";
    private const string SetShowRemovedCardsInDeckViewMethodName = "SetShowRemovedCardsInDeckView";
    private const string SetShowAllMetaCardsInNotInDeckViewMethodName =
        "SetShowAllMetaCardsInNotInDeckView";
    private const string SetShowEnemyStatsOnHoverMethodName = "SetShowEnemyStatsOnHover";
    private const string SetShowCardStatsDuringCombatMethodName = "SetShowCardStatsDuringCombat";
    private const string SetHideNonCombatRelicStatsMethodName = "SetHideNonCombatRelicStats";
    private const string SetShowCombatOnlyRelicsAtCombatScreenMethodName = "SetShowCombatOnlyRelicsAtCombatScreen";
    private const string SetVerboseHandStatsEnabledMethodName = "SetVerboseHandStatsEnabled";
    private const string SetOptionMethodName = "SetOption";
    private const string RegisterCoreHooksMethodName = "RegisterCoreHooks";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static Type? _bridgeType;
    private static MethodInfo? _getCurrentOptionsJsonMethod;
    private static MethodInfo? _setViewStatsToggleEnabledMethod;
    private static MethodInfo? _setShowRemovedCardsInDeckViewMethod;
    private static MethodInfo? _setShowAllMetaCardsInNotInDeckViewMethod;
    private static MethodInfo? _setShowEnemyStatsOnHoverMethod;
    private static MethodInfo? _setShowCardStatsDuringCombatMethod;
    private static MethodInfo? _setHideNonCombatRelicStatsMethod;
    private static MethodInfo? _setShowCombatOnlyRelicsAtCombatScreenMethod;
    private static MethodInfo? _setVerboseHandStatsEnabledMethod;
    private static MethodInfo? _setOptionMethod;
    private static readonly HashSet<string> LoggedSetOptionFailures = new(StringComparer.Ordinal);
    private static bool _loggedMissingBridge;
    private static bool _loggedRefreshFailure;
    private static bool _loggedToggleFailure;
    private static bool _loggedShowRemovedCardsFailure;
    private static bool _loggedShowAllMetaCardsFailure;
    private static bool _loggedShowEnemyStatsFailure;
    private static bool _loggedShowCardStatsDuringCombatFailure;
    private static bool _loggedHideNonCombatRelicStatsFailure;
    private static bool _loggedShowCombatOnlyRelicsAtCombatScreenFailure;
    private static bool _loggedVerboseHandStatsFailure;

    public static RuntimeOptions Current { get; private set; } = new();

    public static RuntimeOptions Refresh()
    {
        try
        {
            var getOptionsMethod = ResolveGetCurrentOptionsJsonMethod();
            if (getOptionsMethod == null) return Current;

            var json = getOptionsMethod.Invoke(null, null) as string;
            if (string.IsNullOrWhiteSpace(json)) return Current;

            Current = JsonSerializer.Deserialize<RuntimeOptions>(json, JsonOptions) ?? new RuntimeOptions();
            _loggedRefreshFailure = false;
        }
        catch (Exception e)
        {
            if (!_loggedRefreshFailure)
            {
                CoreMain.Logger.Warn($"RuntimeOptionsProvider.Refresh failed: {e.Message}");
                _loggedRefreshFailure = true;
            }
        }

        return Current;
    }

    public static void SetViewStatsToggleEnabled(bool isEnabled)
    {
        try
        {
            var setToggleMethod = ResolveSetViewStatsToggleEnabledMethod();
            setToggleMethod?.Invoke(null, new object?[] { isEnabled });
            _loggedToggleFailure = false;
        }
        catch (Exception e)
        {
            if (!_loggedToggleFailure)
            {
                CoreMain.Logger.Warn($"RuntimeOptionsProvider.SetViewStatsToggleEnabled failed: {e.Message}");
                _loggedToggleFailure = true;
            }
        }

        Refresh();
    }

    public static void SetShowRemovedCardsInDeckView(bool isEnabled)
    {
        try
        {
            var setShowRemovedCardsMethod = ResolveSetShowRemovedCardsInDeckViewMethod();
            setShowRemovedCardsMethod?.Invoke(null, new object?[] { isEnabled });
            _loggedShowRemovedCardsFailure = false;
        }
        catch (Exception e)
        {
            if (!_loggedShowRemovedCardsFailure)
            {
                CoreMain.Logger.Warn($"RuntimeOptionsProvider.SetShowRemovedCardsInDeckView failed: {e.Message}");
                _loggedShowRemovedCardsFailure = true;
            }
        }

        Refresh();
    }

    public static void SetShowAllMetaCardsInNotInDeckView(bool isEnabled)
    {
        try
        {
            var method = ResolveSetShowAllMetaCardsInNotInDeckViewMethod();
            if (method == null)
            {
                // The stable Loader cannot gain a new bridge method through a
                // Core hot reload. Keep the option functional in memory until
                // the next full game restart loads the updated Loader.
                Current.ShowAllMetaCardsInNotInDeckView = isEnabled;
                return;
            }

            method.Invoke(null, new object?[] { isEnabled });
            _loggedShowAllMetaCardsFailure = false;
        }
        catch (Exception e)
        {
            if (!_loggedShowAllMetaCardsFailure)
            {
                CoreMain.Logger.Warn(
                    $"RuntimeOptionsProvider.SetShowAllMetaCardsInNotInDeckView failed: {e.Message}");
                _loggedShowAllMetaCardsFailure = true;
            }
        }

        Refresh();
    }

    public static void SetShowEnemyStatsOnHover(bool isEnabled)
    {
        try
        {
            var setShowEnemyStatsMethod = ResolveSetShowEnemyStatsOnHoverMethod();
            setShowEnemyStatsMethod?.Invoke(null, new object?[] { isEnabled });
            _loggedShowEnemyStatsFailure = false;
        }
        catch (Exception e)
        {
            if (!_loggedShowEnemyStatsFailure)
            {
                CoreMain.Logger.Warn($"RuntimeOptionsProvider.SetShowEnemyStatsOnHover failed: {e.Message}");
                _loggedShowEnemyStatsFailure = true;
            }
        }

        Refresh();
    }

    public static void SetShowCardStatsDuringCombat(bool isEnabled)
    {
        try
        {
            var setShowCardStatsDuringCombatMethod = ResolveSetShowCardStatsDuringCombatMethod();
            setShowCardStatsDuringCombatMethod?.Invoke(null, new object?[] { isEnabled });
            _loggedShowCardStatsDuringCombatFailure = false;
        }
        catch (Exception e)
        {
            if (!_loggedShowCardStatsDuringCombatFailure)
            {
                CoreMain.Logger.Warn($"RuntimeOptionsProvider.SetShowCardStatsDuringCombat failed: {e.Message}");
                _loggedShowCardStatsDuringCombatFailure = true;
            }
        }

        Refresh();
    }

    public static void SetHideNonCombatRelicStats(bool isEnabled)
    {
        try
        {
            var method = ResolveSetHideNonCombatRelicStatsMethod();
            method?.Invoke(null, new object?[] { isEnabled });
            _loggedHideNonCombatRelicStatsFailure = false;
        }
        catch (Exception e)
        {
            if (!_loggedHideNonCombatRelicStatsFailure)
            {
                CoreMain.Logger.Warn($"RuntimeOptionsProvider.SetHideNonCombatRelicStats failed: {e.Message}");
                _loggedHideNonCombatRelicStatsFailure = true;
            }
        }

        Refresh();
    }

    public static void SetShowCombatOnlyRelicsAtCombatScreen(bool isEnabled)
    {
        try
        {
            var method = ResolveSetShowCombatOnlyRelicsAtCombatScreenMethod();
            method?.Invoke(null, new object?[] { isEnabled });
            _loggedShowCombatOnlyRelicsAtCombatScreenFailure = false;
        }
        catch (Exception e)
        {
            if (!_loggedShowCombatOnlyRelicsAtCombatScreenFailure)
            {
                CoreMain.Logger.Warn($"RuntimeOptionsProvider.SetShowCombatOnlyRelicsAtCombatScreen failed: {e.Message}");
                _loggedShowCombatOnlyRelicsAtCombatScreenFailure = true;
            }
        }

        Refresh();
    }

    public static void SetVerboseHandStatsEnabled(bool isEnabled)
    {
        try
        {
            var setVerboseMethod = ResolveSetVerboseHandStatsEnabledMethod();
            setVerboseMethod?.Invoke(null, new object?[] { isEnabled });
            _loggedVerboseHandStatsFailure = false;
        }
        catch (Exception e)
        {
            if (!_loggedVerboseHandStatsFailure)
            {
                CoreMain.Logger.Warn($"RuntimeOptionsProvider.SetVerboseHandStatsEnabled failed: {e.Message}");
                _loggedVerboseHandStatsFailure = true;
            }
        }

        Refresh();
    }

    /// <summary>
    /// True when the running Loader exposes the generic <c>SetOption</c>
    /// bridge. A Core hot reload cannot add it — the Loader only updates on a
    /// full game restart — so false means "needs a game restart", not an error.
    /// </summary>
    public static bool CanSetOption => ResolveSetOptionMethod() != null;

    public static bool TrySetOption(string name, bool value) =>
        TrySetOption(name, value ? "true" : "false");

    /// <summary>
    /// Writes one setting through the Loader's validated generic setter.
    /// <paramref name="name"/> is the BaseLib property name, which the
    /// <see cref="RuntimeOptions"/> properties mirror. Returns false without
    /// throwing when the Loader predates the setter or rejects the value.
    /// </summary>
    public static bool TrySetOption(string name, string value)
    {
        var method = ResolveSetOptionMethod();
        if (method == null) return false;

        var applied = false;
        try
        {
            applied = method.Invoke(null, new object?[] { name, value }) is true;
            if (applied)
                LoggedSetOptionFailures.Remove(name);
            else
                LogSetOptionFailureOnce(name, $"the Loader rejected '{value}'");
        }
        catch (Exception e)
        {
            LogSetOptionFailureOnce(name, e.InnerException?.Message ?? e.Message);
        }

        Refresh();
        return applied;
    }

    /// <summary>
    /// Hands the Loader this Core's Mods-page hooks (or clears them with
    /// nulls). Returns false when the running Loader predates
    /// <c>RegisterCoreHooks</c>; its Mods page then simply has no Core
    /// section, and only a full game restart loads a Loader that has one.
    /// </summary>
    public static bool RegisterCoreHooks(
        Action? optionsChanged,
        Action<Godot.Control, Func<string, Godot.Control>, Func<string, Godot.Control>, Func<string, string, Action?, Godot.Control>>? settingsSection)
    {
        try
        {
            var method = ResolveBridgeType()?.GetMethod(
                RegisterCoreHooksMethodName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types:
                [
                    typeof(Action),
                    typeof(Action<Godot.Control, Func<string, Godot.Control>, Func<string, Godot.Control>, Func<string, string, Action, Godot.Control>>),
                ],
                modifiers: null);
            if (method == null) return false;

            method.Invoke(null, new object?[] { optionsChanged, settingsSection });
            return true;
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"RuntimeOptionsProvider.RegisterCoreHooks failed: {e.InnerException?.Message ?? e.Message}");
            return false;
        }
    }

    private static void LogSetOptionFailureOnce(string name, string reason)
    {
        if (LoggedSetOptionFailures.Add(name))
            CoreMain.Logger.Warn($"RuntimeOptionsProvider.TrySetOption({name}) failed: {reason}");
    }

    private static MethodInfo? ResolveSetOptionMethod()
    {
        _setOptionMethod ??= ResolveBridgeType()?.GetMethod(
            SetOptionMethodName,
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(string)],
            modifiers: null);
        return _setOptionMethod;
    }

    private static MethodInfo? ResolveGetCurrentOptionsJsonMethod()
    {
        _getCurrentOptionsJsonMethod ??= ResolveBridgeType()?.GetMethod(
            GetCurrentOptionsJsonMethodName,
            BindingFlags.Public | BindingFlags.Static);
        return _getCurrentOptionsJsonMethod;
    }

    private static MethodInfo? ResolveSetViewStatsToggleEnabledMethod()
    {
        _setViewStatsToggleEnabledMethod ??= ResolveBridgeType()?.GetMethod(
            SetViewStatsToggleEnabledMethodName,
            BindingFlags.Public | BindingFlags.Static);
        return _setViewStatsToggleEnabledMethod;
    }

    private static MethodInfo? ResolveSetShowRemovedCardsInDeckViewMethod()
    {
        _setShowRemovedCardsInDeckViewMethod ??= ResolveBridgeType()?.GetMethod(
            SetShowRemovedCardsInDeckViewMethodName,
            BindingFlags.Public | BindingFlags.Static);
        return _setShowRemovedCardsInDeckViewMethod;
    }

    private static MethodInfo? ResolveSetShowAllMetaCardsInNotInDeckViewMethod()
    {
        _setShowAllMetaCardsInNotInDeckViewMethod ??= ResolveBridgeType()?.GetMethod(
            SetShowAllMetaCardsInNotInDeckViewMethodName,
            BindingFlags.Public | BindingFlags.Static);
        return _setShowAllMetaCardsInNotInDeckViewMethod;
    }

    private static MethodInfo? ResolveSetShowEnemyStatsOnHoverMethod()
    {
        _setShowEnemyStatsOnHoverMethod ??= ResolveBridgeType()?.GetMethod(
            SetShowEnemyStatsOnHoverMethodName,
            BindingFlags.Public | BindingFlags.Static);
        return _setShowEnemyStatsOnHoverMethod;
    }

    private static MethodInfo? ResolveSetShowCardStatsDuringCombatMethod()
    {
        _setShowCardStatsDuringCombatMethod ??= ResolveBridgeType()?.GetMethod(
            SetShowCardStatsDuringCombatMethodName,
            BindingFlags.Public | BindingFlags.Static);
        return _setShowCardStatsDuringCombatMethod;
    }

    private static MethodInfo? ResolveSetVerboseHandStatsEnabledMethod()
    {
        _setVerboseHandStatsEnabledMethod ??= ResolveBridgeType()?.GetMethod(
            SetVerboseHandStatsEnabledMethodName,
            BindingFlags.Public | BindingFlags.Static);
        return _setVerboseHandStatsEnabledMethod;
    }

    private static MethodInfo? ResolveSetHideNonCombatRelicStatsMethod()
    {
        _setHideNonCombatRelicStatsMethod ??= ResolveBridgeType()?.GetMethod(
            SetHideNonCombatRelicStatsMethodName,
            BindingFlags.Public | BindingFlags.Static);
        return _setHideNonCombatRelicStatsMethod;
    }

    private static MethodInfo? ResolveSetShowCombatOnlyRelicsAtCombatScreenMethod()
    {
        _setShowCombatOnlyRelicsAtCombatScreenMethod ??= ResolveBridgeType()?.GetMethod(
            SetShowCombatOnlyRelicsAtCombatScreenMethodName,
            BindingFlags.Public | BindingFlags.Static);
        return _setShowCombatOnlyRelicsAtCombatScreenMethod;
    }

    private static Type? ResolveBridgeType()
    {
        if (_bridgeType != null) return _bridgeType;

        _bridgeType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(BridgeTypeName, throwOnError: false))
            .FirstOrDefault(type => type != null);

        if (_bridgeType == null && !_loggedMissingBridge)
        {
            CoreMain.Logger.Warn("RuntimeOptionsProvider could not find the loader bridge; using default options.");
            _loggedMissingBridge = true;
        }

        return _bridgeType;
    }
}
