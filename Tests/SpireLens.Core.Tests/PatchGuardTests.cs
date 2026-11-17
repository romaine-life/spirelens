using SpireLens.Core;
using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>
/// Pins <see cref="PatchGuard"/>'s two guarantees — a patch body never
/// propagates a throw into game code, and a wedged hot hook is surfaced once
/// then throttled rather than flooding the log — plus a source-scan convention
/// that keeps the three template patches routed through the guard.
/// </summary>
[Collection("PatchGuardState")]
public class PatchGuardTests
{
    private static readonly string RepoRoot =
        RepoPaths.Root;

    [Fact]
    public void Run_ExecutesBody_WhenNoThrow()
    {
        PatchGuard.ResetForTest();

        bool ran = false;
        PatchGuard.Run("site.ok", () => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public void Run_SwallowsException_DoesNotPropagate()
    {
        PatchGuard.ResetForTest();

        var thrown = Record.Exception(() =>
            PatchGuard.Run("site.boom", () => throw new InvalidOperationException("boom")));

        Assert.Null(thrown);
    }

    [Fact]
    public void Run_LogsFirstFailure_ThenThrottlesWithinWindow_AndFoldsSuppressedCount()
    {
        PatchGuard.ResetForTest();
        var logs = new List<string>();
        PatchGuard.LogSink = logs.Add;
        PatchGuard.ThrottleWindow = TimeSpan.FromSeconds(30);
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        PatchGuard.Clock = () => clock;

        // Five failures at the same instant: only the first surfaces.
        for (int i = 0; i < 5; i++)
            PatchGuard.Run("site.hot", () => throw new InvalidOperationException("x"));

        Assert.Single(logs);
        Assert.Contains("site.hot", logs[0]);

        // Once the window elapses, the next failure surfaces again and reports
        // how many were suppressed in between (the 4 after the first).
        clock = clock.AddSeconds(31);
        PatchGuard.Run("site.hot", () => throw new InvalidOperationException("x"));

        Assert.Equal(2, logs.Count);
        Assert.Contains("4 similar suppressed", logs[1]);

        PatchGuard.ResetForTest();
    }

    [Fact]
    public void Run_ThrottlesPerSiteIndependently()
    {
        PatchGuard.ResetForTest();
        var logs = new List<string>();
        PatchGuard.LogSink = logs.Add;
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        PatchGuard.Clock = () => clock;

        // Distinct sites do not share a throttle: both first-failures surface.
        PatchGuard.Run("site.a", () => throw new InvalidOperationException());
        PatchGuard.Run("site.b", () => throw new InvalidOperationException());

        Assert.Equal(2, logs.Count);

        PatchGuard.ResetForTest();
    }

    [Fact]
    public void TemplatePatches_RouteThroughPatchGuard()
    {
        var patchesDir = Path.Combine(RepoRoot, "Core", "Patches");

        foreach (var file in new[]
                 {
                     "CombatHistoryAddPatch.cs",
                     "RunHistoryUtilitiesCreateEntryPatch.cs",
                     "AkabekoStatsPatch.cs",
                 })
        {
            var text = File.ReadAllText(Path.Combine(patchesDir, file));
            Assert.Contains("PatchGuard.Run", text);
        }
    }

    [Fact]
    public void Observe_SelfGuardsThroughPatchGuard()
    {
        // RunTracker.Observe wraps its whole body and never rethrows, so a guard
        // only at the CombatHistoryAddPatch caller would have a dead throttle.
        // The real, throttled guard for the busiest hook must live in Observe
        // itself — pin that at the source level so it can't silently regress.
        var runTracker = File.ReadAllText(Path.Combine(RepoRoot, "Core", "RunTracker.cs"));
        Assert.Contains("PatchGuard.Run(\"RunTracker.Observe\"", runTracker);
        // And the old un-throttled swallow it replaced must not creep back in.
        Assert.DoesNotContain("RunTracker.Observe failed", runTracker);
    }
}

/// <summary>
/// Serializes the PatchGuard tests: they mutate the shared static throttle
/// state, clock, and sink, so they must not interleave with each other.
/// </summary>
[CollectionDefinition("PatchGuardState", DisableParallelization = true)]
public sealed class PatchGuardStateCollection { }
