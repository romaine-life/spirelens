using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SpireLens.Core.Replay.Playback;

/// <summary>One run of a playlist, as the file names it.</summary>
public sealed record PlaylistRunRef(long GameStartTime, string? Label);

/// <summary>A saved playlist (<c>playlists/&lt;slug&gt;.json</c>).</summary>
public sealed record ReplayPlaylist(string Slug, string Name, IReadOnlyList<PlaylistRunRef> Runs, string? UpdatedAtUtc);

/// <summary>A "play now" request (<c>playlists/play-now.json</c>): a saved playlist by slug, or inline runs.</summary>
public sealed record PlayNowRequest(string? PlaylistSlug, IReadOnlyList<PlaylistRunRef>? Runs, string? RequestedAtUtc);

/// <summary>
/// The playlist file contract shared with the run-database web page
/// (<c>spirelens-db serve</c>); see docs/replay-player.md, "Playlists".
/// Unknown fields are ignored; a file that can't be read is skipped with a
/// reason. Pure and game-independent, unit tested.
/// </summary>
public static class PlaylistFormat
{
    public const int FormatVersion = 1;
    public const string PlayNowFileName = "play-now.json";

    public static ReplayPlaylist? ParsePlaylist(string json, string slug, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "not a JSON object"; return null; }
            if (!VersionOk(root, out error)) return null;
            if (!root.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            {
                error = "no runs array";
                return null;
            }
            string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? slug
                : slug;
            string? updated = root.TryGetProperty("updated_at_utc", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()
                : null;
            return new ReplayPlaylist(slug, name, ParseRuns(runs), updated);
        }
        catch (JsonException e)
        {
            error = "invalid JSON: " + e.Message;
            return null;
        }
    }

    public static PlayNowRequest? ParsePlayNow(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "not a JSON object"; return null; }
            if (!VersionOk(root, out error)) return null;
            string? requested = root.TryGetProperty("requested_at_utc", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            if (root.TryGetProperty("playlist", out var p) && p.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(p.GetString()))
                return new PlayNowRequest(p.GetString(), null, requested);
            if (root.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
                return new PlayNowRequest(null, ParseRuns(runs), requested);
            error = "neither a playlist slug nor a runs array";
            return null;
        }
        catch (JsonException e)
        {
            error = "invalid JSON: " + e.Message;
            return null;
        }
    }

    /// <summary>The slug a playlist file name stands for, or null for files that aren't playlists.</summary>
    public static string? SlugFromFileName(string fileName)
    {
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(fileName, PlayNowFileName, StringComparison.OrdinalIgnoreCase)) return null;
        var slug = fileName.Substring(0, fileName.Length - ".json".Length);
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>A slug is a plain file stem: no path separators or parent references.</summary>
    public static bool IsSafeSlug(string slug) =>
        slug.Length > 0
        && slug.IndexOfAny(new[] { '/', '\\', ':' }) < 0
        && !slug.Contains("..", StringComparison.Ordinal);

    private static bool VersionOk(JsonElement root, out string? error)
    {
        error = null;
        if (!root.TryGetProperty("format_version", out var v)) return true;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int version) || version < 1)
        {
            error = "bad format_version";
            return false;
        }
        return true;
    }

    private static List<PlaylistRunRef> ParseRuns(JsonElement runs)
    {
        var result = new List<PlaylistRunRef>();
        foreach (var run in runs.EnumerateArray())
        {
            if (run.ValueKind != JsonValueKind.Object) continue;
            if (!run.TryGetProperty("game_start_time", out var g) || g.ValueKind != JsonValueKind.Number
                || !g.TryGetInt64(out long gst) || gst <= 0)
                continue;
            string? label = run.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString()
                : null;
            result.Add(new PlaylistRunRef(gst, label));
        }
        return result;
    }
}

/// <summary>Reads saved playlists from <c>user://SpireLens/playlists/</c>.</summary>
internal static class ReplayPlaylistLibrary
{
    internal static Func<string> DirProvider = () =>
        Godot.ProjectSettings.GlobalizePath("user://SpireLens/playlists/");

    public static string Dir => DirProvider();
    public static string PlayNowPath => Path.Combine(Dir, PlaylistFormat.PlayNowFileName);

    public static List<ReplayPlaylist> List()
    {
        var result = new List<ReplayPlaylist>();
        if (!Directory.Exists(Dir)) return result;
        foreach (var path in Directory.GetFiles(Dir, "*.json"))
        {
            var slug = PlaylistFormat.SlugFromFileName(Path.GetFileName(path));
            if (slug == null) continue;
            var playlist = Load(slug);
            if (playlist != null) result.Add(playlist);
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static ReplayPlaylist? Load(string slug)
    {
        if (!PlaylistFormat.IsSafeSlug(slug))
        {
            CoreMain.Logger.Warn($"Playlists: refusing unsafe playlist name '{slug}'");
            return null;
        }
        string path = Path.Combine(Dir, slug + ".json");
        try
        {
            if (!File.Exists(path)) return null;
            var playlist = PlaylistFormat.ParsePlaylist(File.ReadAllText(path), slug, out var error);
            if (playlist == null) CoreMain.Logger.Warn($"Playlists: skipping {Path.GetFileName(path)}: {error}");
            return playlist;
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"Playlists: skipping {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    /// <summary>Resolve a playlist's runs to recordings (null listing = recording missing).</summary>
    public static List<(PlaylistRunRef Ref, ReplayListing? Listing)> Resolve(IEnumerable<PlaylistRunRef> runs) =>
        runs.Select(r => (r, ReplayLibrary.ListingFor(r.GameStartTime))).ToList();
}
