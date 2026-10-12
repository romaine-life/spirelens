using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using SpireLens.Core.Effects;

namespace SpireLens.Core;

/// <summary>
/// The generic effect-frame framework's tracker half (see
/// <see cref="EffectFrame"/> and <see cref="EffectRegistry"/>).
///
/// Shape of the data:
///   - relic owners credit <c>RelicAggregate.Outcomes</c>;
///   - power owners credit the pooled <c>PowerAggregate.Outcomes</c> and,
///     split by contributed stacks, the <c>AppliedEffectAggregate.Outcomes</c>
///     of each card or relic that supplied the power.
///
/// Combat outcomes stay in the pending combat buffer like every other
/// combat stat; outcomes outside combat (pickups, rest sites, shops) are
/// committed to the run immediately, as the hand-written pickup relics do.
/// </summary>
public static partial class RunTracker
{
    // ---------------- Frames ----------------

    internal static EffectFrame? TryCreateEffectFrame(AbstractModel model, string hookName)
    {
        if (!EffectRegistry.IsEnrolled(model)) return null;

        lock (_lock)
        {
            if (!TryResolveEffectOwnerLocked(model, out var kind, out var ownerId, out var ownerCreature))
                return null;

            bool inCombat = CombatManager.Instance?.IsInProgress == true;
            return new EffectFrame
            {
                Kind = kind,
                OwnerId = ownerId,
                Owner = model,
                OwnerCreature = ownerCreature,
                HookName = hookName,
                Combat = inCombat ? _pendingCombat : null,
                ArmedPlay = inCombat ? FindCurrentlyResolvingCardPlay() : null,
                Shares = model is PowerModel power ? SnapshotEffectPowerSharesLocked(power) : [],
                Parent = EffectFrameContext.Current,
            };
        }
    }

    internal static void CloseEffectFrame(EffectFrame frame)
    {
        lock (_lock)
        {
            try
            {
                bool counts = frame.Kind == EffectOwnerKind.Relic
                    ? frame.Credited || frame.Flashed
                    : frame.Credited;
                if (counts && IsEffectFrameCombatCurrentLocked(frame))
                {
                    AddEffectOutcomeLocked(
                        frame.Kind,
                        frame.OwnerId,
                        frame.Owner,
                        shares: [],
                        EffectOutcomeKeys.Triggers,
                        1m,
                        out bool outOfCombat);
                    frame.CreditedOutOfCombat |= outOfCombat;
                }

                if (frame.CreditedOutOfCombat)
                    SaveCurrentRun();
            }
            catch (Exception e)
            {
                CoreMain.LogDebug($"CloseEffectFrame failed: {e.Message}");
            }
        }
    }

    private static bool TryResolveEffectOwnerLocked(
        AbstractModel model,
        out EffectOwnerKind kind,
        out string ownerId,
        out Creature? ownerCreature)
    {
        kind = default;
        ownerId = "";
        ownerCreature = null;

        switch (model)
        {
            case RelicModel relic:
                if (relic.Owner == null || !IsTrackedPlayer(relic.Owner)) return false;
                kind = EffectOwnerKind.Relic;
                ownerId = relic.Id.ToString();
                ownerCreature = relic.Owner.Creature;
                return true;

            case PowerModel power:
                var owner = power.Owner;
                if (owner == null) return false;
                var playerCreature = owner.IsPlayer ? owner : owner.PetOwner?.Creature;
                if (playerCreature == null || !playerCreature.IsPlayer) return false;
                if (!IsTrackedPlayerCreature(playerCreature)) return false;
                kind = EffectOwnerKind.Power;
                ownerId = power.Id.ToString();
                ownerCreature = owner;
                return true;

            default:
                return false;
        }
    }

    private static bool IsEffectFrameCombatCurrentLocked(EffectFrame frame)
        => frame.Combat == null || ReferenceEquals(frame.Combat, _pendingCombat);

    /// <summary>
    /// The innermost frame that may claim an outcome right now. A frame from
    /// a combat that has since been promoted or discarded is stale. A card
    /// play that started after the frame armed (an autoplay the owner
    /// caused) shadows it: that card owns its own damage, block and draws.
    /// </summary>
    private static bool TryGetLiveEffectFrameLocked(out EffectFrame frame)
    {
        frame = null!;
        var current = EffectFrameContext.Current;
        if (current == null) return false;
        if (!IsEffectFrameCombatCurrentLocked(current)) return false;

        if (current.Combat != null)
        {
            var resolving = FindCurrentlyResolvingCardPlay();
            if (resolving?.Card != null && !ReferenceEquals(resolving, current.ArmedPlay))
                return false;
        }

        frame = current;
        return true;
    }

    private static bool IsEffectFrameReceiver(EffectFrame frame, Creature? receiver)
        => receiver == null || ReferenceEquals(receiver, frame.OwnerCreature)
            || (frame.OwnerCreature?.PetOwner?.Creature is { } petOwner
                && ReferenceEquals(receiver, petOwner));

    /// <summary>Credit an outcome to the live frame when the receiver is the
    /// frame's owner. Returns true when the frame claimed it, which tells the
    /// caller to skip its resolving-card fallback.</summary>
    private static bool TryCreditEffectFrameLocked(Creature? receiver, string key, decimal amount)
    {
        if (amount == 0m) return false;
        if (!TryGetLiveEffectFrameLocked(out var frame)) return false;
        if (!IsEffectFrameReceiver(frame, receiver)) return false;

        CreditEffectFrameLocked(frame, key, amount);
        return true;
    }

    private static void CreditEffectFrameLocked(EffectFrame frame, string key, decimal amount)
    {
        AddEffectOutcomeLocked(
            frame.Kind,
            frame.OwnerId,
            frame.Owner,
            frame.Shares,
            key,
            amount,
            out bool outOfCombat);
        frame.Credited = true;
        frame.CreditedOutOfCombat |= outOfCombat;
    }

    /// <summary>Credit a specific frame (captured before an awaited command
    /// completed), for observers that measure after the await.</summary>
    internal static void CreditEffectFrame(EffectFrame frame, Creature? receiver, string key, decimal amount)
    {
        if (amount == 0m) return;
        lock (_lock)
        {
            try
            {
                if (!IsEffectFrameCombatCurrentLocked(frame)) return;
                if (!IsEffectFrameReceiver(frame, receiver)) return;
                CreditEffectFrameLocked(frame, key, amount);
            }
            catch (Exception e)
            {
                CoreMain.LogDebug($"CreditEffectFrame failed: {e.Message}");
            }
        }
    }

    /// <summary>Credit an enrolled owner for a value it modified outside any
    /// frame (draw and max-Energy modifiers).</summary>
    internal static void CreditEffectOwnerDirect(AbstractModel model, Player? player, string key, decimal amount)
    {
        if (amount == 0m || !EffectRegistry.IsEnrolled(model)) return;
        lock (_lock)
        {
            try
            {
                if (player != null && !IsTrackedPlayer(player)) return;
                if (!TryResolveEffectOwnerLocked(model, out var kind, out var ownerId, out _)) return;
                var shares = model is PowerModel power
                    ? SnapshotEffectPowerSharesLocked(power)
                    : (IReadOnlyList<EffectSourceShare>)[];
                AddEffectOutcomeLocked(kind, ownerId, model, shares, key, amount, out bool outOfCombat);
                if (outOfCombat) SaveCurrentRun();
            }
            catch (Exception e)
            {
                CoreMain.LogDebug($"CreditEffectOwnerDirect failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// The single write path. In combat the outcome lands in the pending
    /// combat buffer; outside combat it is committed to the run (the caller
    /// saves).
    /// </summary>
    private static void AddEffectOutcomeLocked(
        EffectOwnerKind kind,
        string ownerId,
        AbstractModel owner,
        IReadOnlyList<EffectSourceShare> shares,
        string key,
        decimal amount,
        out bool outOfCombat)
    {
        outOfCombat = false;
        bool inCombat = _pendingCombat != null && CombatManager.Instance?.IsInProgress == true;

        if (kind == EffectOwnerKind.Relic)
        {
            RelicAggregate agg;
            if (inCombat)
            {
                agg = GetOrCreatePendingRelicAggregateLocked(ownerId);
            }
            else
            {
                agg = GetOrCreateCurrentRunRelicAggregateLocked(ownerId);
                outOfCombat = true;
            }

            agg.Outcomes = AddEffectOutcome(agg.Outcomes, key, amount);
            return;
        }

        // Powers only resolve in combat; anything else is a stray.
        if (!inCombat || owner is not PowerModel power) return;

        var powerAgg = GetOrCreatePowerAggregate(
            _pendingCombat!.MetaStats,
            ownerId,
            GetPowerDisplayName(power));
        powerAgg.Outcomes = AddEffectOutcome(powerAgg.Outcomes, key, amount);

        if (key == EffectOutcomeKeys.Triggers) return;
        foreach (var share in shares)
        {
            if (share.Share <= 0m) continue;
            decimal portion = amount * share.Share;
            AppliedEffectAggregate? effect = null;
            if (share.CardInstanceId != null)
            {
                var cardAgg = GetOrCreateAggregate(_pendingCombat, share.CardInstanceId);
                effect = GetOrCreateAppliedEffect(cardAgg, power);
            }
            else if (share.RelicId != null)
            {
                var relicAgg = GetOrCreatePendingRelicAggregateLocked(share.RelicId);
                effect = GetOrCreateAppliedEffect(relicAgg, power);
            }

            if (effect != null)
                effect.Outcomes = AddEffectOutcome(effect.Outcomes, key, portion);
        }
    }

    internal static Dictionary<string, decimal> AddEffectOutcome(
        Dictionary<string, decimal>? bag,
        string key,
        decimal amount)
    {
        bag ??= new Dictionary<string, decimal>(StringComparer.Ordinal);
        bag.TryGetValue(key, out var current);
        bag[key] = current + amount;
        return bag;
    }

    internal static Dictionary<string, decimal>? MergeEffectOutcomes(
        Dictionary<string, decimal>? target,
        Dictionary<string, decimal>? source)
    {
        if (source == null || source.Count == 0) return target;
        foreach (var (key, value) in source)
            target = AddEffectOutcome(target, key, value);
        return target;
    }

    // ---------------- Power contributor ledger ----------------

    /// <summary>
    /// Seeds or reduces the contributor ledger of an enrolled power from a
    /// <c>PowerReceivedEntry</c>, then lets a live frame claim the
    /// application. Returns true when a frame claimed it, so the caller
    /// skips crediting the resolving card for a stack it did not apply.
    /// </summary>
    private static bool RecordEffectPowerReceivedLocked(
        PowerModel power,
        decimal amount,
        Creature? target,
        CardPlay? causingPlay)
    {
        bool hasLiveFrame = TryGetLiveEffectFrameLocked(out var frame);

        if (_pendingCombat != null && EffectRegistry.IsEnrolledPower(power))
        {
            var ledger = GetEffectPowerLedgerLocked(power);
            if (amount > 0m)
            {
                if (hasLiveFrame && frame.Kind == EffectOwnerKind.Relic)
                {
                    ledger.Add(new EffectStackContribution(null, frame.OwnerId, amount));
                }
                else if (hasLiveFrame && frame.Kind == EffectOwnerKind.Power)
                {
                    // Power-made stacks inherit the parent power's sources.
                    foreach (var share in frame.Shares)
                    {
                        ledger.Add(new EffectStackContribution(
                            share.CardInstanceId,
                            share.RelicId,
                            amount * share.Share));
                    }
                    if (frame.Shares.Count == 0)
                        ledger.Add(new EffectStackContribution(null, null, amount));
                }
                else if (causingPlay?.Card != null)
                {
                    ledger.Add(new EffectStackContribution(
                        GetOrAssignInstanceId(causingPlay.Card),
                        null,
                        amount));
                }
                else
                {
                    ledger.Add(new EffectStackContribution(null, null, amount));
                }
            }
            else if (amount < 0m)
            {
                ReduceEffectPowerLedger(ledger, -amount);
            }
        }

        if (!hasLiveFrame || amount <= 0m || target == null) return false;
        if (ReferenceEquals(frame.Owner, power)) return false; // its own stack bookkeeping

        var powerId = power.Id.ToString();
        var key = target.IsEnemy
            ? EffectOutcomeKeys.EnemyPower(powerId)
            : EffectOutcomeKeys.SelfPower(powerId);
        CreditEffectFrameLocked(frame, key, amount);
        return true;
    }

    private static List<EffectStackContribution> GetEffectPowerLedgerLocked(PowerModel power)
    {
        var ledgers = _pendingCombat!.EffectPowerLedgers;
        if (!ledgers.TryGetValue(power, out var ledger))
        {
            ledger = new List<EffectStackContribution>();
            ledgers[power] = ledger;
        }
        return ledger;
    }

    internal static void ReduceEffectPowerLedger(List<EffectStackContribution> ledger, decimal reduction)
    {
        decimal total = ledger.Sum(c => c.Stacks);
        if (total <= 0m) return;
        decimal keep = Math.Max(0m, total - reduction) / total;
        for (int i = 0; i < ledger.Count; i++)
            ledger[i] = ledger[i] with { Stacks = ledger[i].Stacks * keep };
        ledger.RemoveAll(c => c.Stacks <= 0m);
    }

    internal static IReadOnlyList<EffectSourceShare> ComputeEffectSourceShares(
        IReadOnlyList<EffectStackContribution> ledger)
    {
        decimal total = ledger.Sum(c => c.Stacks);
        if (total <= 0m) return [];

        // One share per distinct source, in first-contribution order.
        var result = new List<EffectSourceShare>();
        var index = new Dictionary<(string?, string?), int>();
        foreach (var contribution in ledger)
        {
            var sourceKey = (contribution.CardInstanceId, contribution.RelicId);
            decimal share = contribution.Stacks / total;
            if (index.TryGetValue(sourceKey, out var i))
            {
                result[i] = result[i] with { Share = result[i].Share + share };
            }
            else
            {
                index[sourceKey] = result.Count;
                result.Add(new EffectSourceShare(contribution.CardInstanceId, contribution.RelicId, share));
            }
        }
        return result;
    }

    private static IReadOnlyList<EffectSourceShare> SnapshotEffectPowerSharesLocked(PowerModel power)
    {
        if (_pendingCombat == null) return [];
        return _pendingCombat.EffectPowerLedgers.TryGetValue(power, out var ledger)
            ? ComputeEffectSourceShares(ledger)
            : [];
    }

    // ---------------- Observers ----------------

    /// <summary>Damage dealt (or self-inflicted) with no card source while
    /// an enrolled owner's hook is resolving.</summary>
    internal static bool TryRecordEffectFrameDamage(DamageReceivedEntry entry)
    {
        lock (_lock)
        {
            try
            {
                if (!TryGetLiveEffectFrameLocked(out var frame)) return false;

                var receiver = entry.Receiver;
                var dealer = entry.Dealer;
                bool dealtByOwner = dealer == null || IsEffectFrameReceiver(frame, dealer);
                if (!dealtByOwner || receiver == null) return false;

                var result = entry.Result;
                if (receiver.IsPlayer)
                {
                    if (!IsEffectFrameReceiver(frame, receiver)) return false;
                    CreditEffectFrameLocked(frame, EffectOutcomeKeys.HpLost, Math.Max(0, result.UnblockedDamage));
                    return true;
                }

                CreditEffectFrameLocked(frame, EffectOutcomeKeys.Damage, Math.Max(0, result.UnblockedDamage));
                if (result.BlockedDamage > 0)
                    CreditEffectFrameLocked(frame, EffectOutcomeKeys.DamageBlocked, result.BlockedDamage);
                if (result.OverkillDamage > 0)
                    CreditEffectFrameLocked(frame, EffectOutcomeKeys.Overkill, result.OverkillDamage);
                CreditEffectFrameLocked(frame, EffectOutcomeKeys.Hits, 1m);
                if (result.WasTargetKilled)
                    CreditEffectFrameLocked(frame, EffectOutcomeKeys.Kills, 1m);
                return true;
            }
            catch (Exception e)
            {
                CoreMain.LogDebug($"TryRecordEffectFrameDamage failed: {e.Message}");
                return false;
            }
        }
    }

    /// <summary>Block a framed owner gave its own creature. The block chunk
    /// keeps the relic id so absorbed/wasted block lands on the relic.</summary>
    private static bool TryClaimEffectFrameBlockLocked(BlockGainedEntry entry, out string? relicId)
    {
        relicId = null;
        if (entry.CardPlay?.Card != null) return false;
        if (!TryGetLiveEffectFrameLocked(out var frame)) return false;
        if (!IsEffectFrameReceiver(frame, entry.Receiver)) return false;

        CreditEffectFrameLocked(frame, EffectOutcomeKeys.Block, entry.Amount);
        if (frame.Kind == EffectOwnerKind.Relic) relicId = frame.OwnerId;
        return true;
    }

    internal static void RecordEffectFrameHeal(Creature creature, decimal restored)
    {
        if (restored <= 0m) return;
        lock (_lock)
        {
            try
            {
                TryCreditEffectFrameLocked(creature, EffectOutcomeKeys.HpHealed, restored);
            }
            catch (Exception e)
            {
                CoreMain.LogDebug($"RecordEffectFrameHeal failed: {e.Message}");
            }
        }
    }

    internal static void RecordEffectFrameOrbChanneled(Creature? channeler)
    {
        lock (_lock)
        {
            try
            {
                TryCreditEffectFrameLocked(channeler, EffectOutcomeKeys.OrbsChanneled, 1m);
            }
            catch (Exception e)
            {
                CoreMain.LogDebug($"RecordEffectFrameOrbChanneled failed: {e.Message}");
            }
        }
    }

    /// <summary>A card-level change (generated, upgraded, added, removed)
    /// made while an enrolled owner's hook resolves.</summary>
    internal static void RecordEffectFrameCardChange(CardModel? card, string key, decimal amount = 1m)
    {
        if (card == null || amount == 0m) return;
        lock (_lock)
        {
            try
            {
                if (!IsTrackedCard(card)) return;
                TryCreditEffectFrameLocked(card.Owner?.Creature, key, amount);
            }
            catch (Exception e)
            {
                CoreMain.LogDebug($"RecordEffectFrameCardChange failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Replays each enrolled max-Energy modifier against the player's base
    /// max Energy at the turn's energy reset, in the order the game's
    /// listeners would see them, and credits each model's marginal change.
    /// </summary>
    internal static void RecordEffectMaxEnergyBonuses(Player? player)
    {
        if (player?.Creature == null) return;
        if (CombatManager.Instance?.IsInProgress != true) return;

        var models = new List<AbstractModel>();
        foreach (var relic in player.Relics)
            if (EffectRegistry.IsEnrolledRelic(relic)) models.Add(relic);
        foreach (var power in player.Creature.Powers)
            if (EffectRegistry.IsEnrolledPower(power)) models.Add(power);
        if (models.Count == 0) return;

        decimal running = player.MaxEnergy;
        foreach (var model in models)
        {
            if (!EffectRegistry.Overrides(model, nameof(AbstractModel.ModifyMaxEnergy))) continue;
            decimal next = model.ModifyMaxEnergy(player, running);
            decimal delta = Math.Truncate(next) - Math.Truncate(running);
            running = next;
            if (delta != 0m)
                CreditEffectOwnerDirect(model, player, EffectOutcomeKeys.MaxEnergyBonus, delta);
        }
    }

    /// <summary>
    /// A relic flash inside its own frame marks the frame as an activation
    /// (counted once when it closes); a flash outside any frame — modifier
    /// relics flash from value hooks — is one activation on its own.
    /// </summary>
    internal static void NoteEffectRelicFlash(RelicModel relic)
    {
        if (!EffectRegistry.IsEnrolledRelic(relic)) return;
        lock (_lock)
        {
            try
            {
                var frame = EffectFrameContext.Current;
                if (frame != null && ReferenceEquals(frame.Owner, relic))
                {
                    frame.Flashed = true;
                    return;
                }

                if (!TryResolveEffectOwnerLocked(relic, out var kind, out var ownerId, out _)) return;
                AddEffectOutcomeLocked(kind, ownerId, relic, [], EffectOutcomeKeys.Triggers, 1m, out bool outOfCombat);
                if (outOfCombat) SaveCurrentRun();
            }
            catch (Exception e)
            {
                CoreMain.LogDebug($"NoteEffectRelicFlash failed: {e.Message}");
            }
        }
    }

    /// <summary>Once per combat, count each enrolled relic the tracked
    /// player holds — the denominator for per-combat averages.</summary>
    private static void RecordEffectRelicCombatsForPlayerLocked(Player player)
    {
        if (_pendingCombat == null) return;
        foreach (var relic in player.Relics)
        {
            if (!EffectRegistry.IsEnrolledRelic(relic)) continue;
            var relicId = relic.Id.ToString();
            if (!_pendingCombat.EffectRelicCombatsCounted.Add(relicId)) continue;
            var agg = GetOrCreatePendingRelicAggregateLocked(relicId);
            agg.Outcomes = AddEffectOutcome(agg.Outcomes, EffectOutcomeKeys.Combats, 1m);
        }
    }

    // ---------------- Queries ----------------

    /// <summary>Committed plus pending pooled outcomes of a power id.</summary>
    internal static Dictionary<string, decimal>? GetPowerEffectOutcomes(string powerId)
    {
        lock (_lock)
        {
            Dictionary<string, decimal>? result = null;
            if (_currentRun?.MetaStats?.PowerAggregates?.TryGetValue(powerId, out var committed) == true)
                result = MergeEffectOutcomes(result, committed.Outcomes);
            if (_pendingCombat?.MetaStats?.PowerAggregates?.TryGetValue(powerId, out var pending) == true)
                result = MergeEffectOutcomes(result, pending.Outcomes);
            return result;
        }
    }
}

/// <summary>Stacks one source put into a power instance.</summary>
internal readonly record struct EffectStackContribution(
    string? CardInstanceId,
    string? RelicId,
    decimal Stacks);
