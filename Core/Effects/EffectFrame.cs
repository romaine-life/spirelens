using System.Collections.Generic;
using System.Threading;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace SpireLens.Core.Effects;

internal enum EffectOwnerKind { Relic, Power }

/// <summary>
/// One credited source of a power's stacks, frozen when its owner callback
/// starts. <see cref="Share"/> is the source's fraction of the tracked stacks
/// (0..1). Card sources carry the SpireLens instance id; relic sources carry
/// the relic id. A source neither can name absorbs its share uncredited.
/// </summary>
internal readonly record struct EffectSourceShare(
    string? CardInstanceId,
    string? RelicId,
    decimal Share);

/// <summary>
/// An owner-specific attribution scope: the relic or power callback that is
/// resolving right now. Pushed by <see cref="Patches.EffectFrameTaskPatch"/>
/// around the owner's own hook (ADR 0001), so the outcome observers that
/// already exist can ask "whose effect is this?" instead of falling back to
/// whichever card happens to be resolving.
///
/// The frame lives in an <see cref="AsyncLocal{T}"/>. A value set in the
/// patch prefix is captured by the async state machine of the original hook,
/// so it flows into every command that hook awaits (GainBlock, Damage, Heal,
/// Draw, PowerCmd.Apply...) and is restored for the caller in the postfix.
/// </summary>
internal sealed class EffectFrame
{
    public required EffectOwnerKind Kind { get; init; }
    public required string OwnerId { get; init; }
    public required AbstractModel Owner { get; init; }
    public required Creature? OwnerCreature { get; init; }
    public required string HookName { get; init; }

    /// <summary>The pending combat the frame was armed in (null out of
    /// combat). A frame whose combat has since been promoted or discarded is
    /// stale and claims nothing.</summary>
    public object? Combat { get; init; }

    /// <summary>The card play that was already resolving when the frame armed
    /// (for example the play that triggered an AfterCardPlayed hook). A
    /// different play resolving later started inside the frame — an autoplay
    /// the owner caused — and owns its own outcomes.</summary>
    public CardPlay? ArmedPlay { get; init; }

    /// <summary>Power frames only: contributor shares at arm time.</summary>
    public IReadOnlyList<EffectSourceShare> Shares { get; init; } = [];

    public EffectFrame? Parent { get; init; }

    // Mutated under RunTracker._lock only.
    public bool Credited { get; set; }
    public bool Flashed { get; set; }
    public bool CreditedOutOfCombat { get; set; }
}

internal static class EffectFrameContext
{
    private static readonly AsyncLocal<EffectFrame?> CurrentFrame = new();

    public static EffectFrame? Current => CurrentFrame.Value;

    public static EffectFrame? Push(EffectFrame frame)
    {
        var previous = CurrentFrame.Value;
        CurrentFrame.Value = frame;
        return previous;
    }

    public static void Restore(EffectFrame? previous) => CurrentFrame.Value = previous;
}
