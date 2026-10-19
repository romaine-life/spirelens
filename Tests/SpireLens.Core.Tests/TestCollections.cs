using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>
/// Serializes tests that mutate RunTracker's process-wide mutable statics
/// (the observed-DamageResult dedup set, the live current/pending run) without
/// restoring them. xUnit runs distinct test classes in parallel by default;
/// enrolling every such class here keeps them from interleaving and flaking
/// (#118). Tests that fully save/restore the static they touch don't need it.
/// </summary>
[CollectionDefinition("RunTrackerState", DisableParallelization = true)]
public sealed class RunTrackerStateCollection { }

/// <summary>
/// Serializes tests that swap ReplayStorage.RootDirProvider. Save/restore is
/// not enough here: one class restoring the Godot-backed default while
/// another is mid-test sends the other into ProjectSettings, which has no
/// engine under test and throws.
/// </summary>
[CollectionDefinition("ReplayStorageRoot", DisableParallelization = true)]
public sealed class ReplayStorageRootCollection { }
