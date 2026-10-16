using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace SpireLens.Core.Replay.Playback;

/// <summary>One recording as the replay list shows it (header.json only).</summary>
public sealed record ReplayListing(
    long GameStartTime,
    string Directory,
    string Date,
    string Character,
    int Ascension,
    string Outcome,
    int? FinalFloor,
    bool StartedMidRun,
    bool Playable,
    string? DisabledReason)
{
    public string Label
    {
        get
        {
            string floor = FinalFloor.HasValue ? $"floor {FinalFloor}" : "floor ?";
            string mid = StartedMidRun ? "  (from mid-run)" : "";
            return $"{Date}   {Character} A{Ascension}   {Outcome.ToUpperInvariant()}   {floor}{mid}";
        }
    }
}

/// <summary>Lists the recordings under the replays directory, newest first, reading header.json only.</summary>
internal static class ReplayLibrary
{
    public static (string? Commit, uint? ModelIdHash) LiveBuild()
    {
        string? commit = null;
        uint? hash = null;
        try { commit = ReleaseInfoManager.Instance.ReleaseInfo?.Commit; } catch { }
        try { hash = ModelIdSerializationCache.Hash; } catch { }
        return (commit, hash);
    }

    public static List<ReplayListing> List(int max = 60)
    {
        var result = new List<ReplayListing>();
        string root = ReplayStorage.RootDir;
        if (!System.IO.Directory.Exists(root)) return result;
        var (liveCommit, liveHash) = LiveBuild();

        foreach (var dir in System.IO.Directory.GetDirectories(root))
        {
            var header = ReplayStorage.TryReadHeader(dir);
            if (header == null) continue;

            var (playable, reason) = ReplayVersionGate.Check(
                header.Game.Commit,
                header.Game.ModelIdHash,
                header.NetType,
                header.Game.Version,
                liveCommit,
                liveHash);
            if (playable && !File.Exists(Path.Combine(dir, "events.jsonl")))
            {
                playable = false;
                reason = "events.jsonl missing";
            }

            string date = "?";
            if (DateTime.TryParse(header.CreatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created))
                date = created.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            string character = header.Players.FirstOrDefault()?.Character ?? "?";
            if (character.StartsWith("CHARACTER.", StringComparison.Ordinal))
                character = character.Substring("CHARACTER.".Length);
            character = character.Length > 1
                ? char.ToUpperInvariant(character[0]) + character.Substring(1).ToLowerInvariant()
                : character;

            result.Add(new ReplayListing(
                header.GameStartTime,
                dir,
                date,
                character,
                header.Ascension,
                header.Outcome,
                header.FinalFloor,
                header.RecordingStartedMidRun,
                playable,
                reason));
        }

        return result
            .OrderByDescending(r => r.GameStartTime)
            .Take(max)
            .ToList();
    }
}
