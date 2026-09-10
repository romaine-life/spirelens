using SpireLens.Core;
using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>
/// Pins the source-owned energy ledger, the counterpart to
/// <see cref="BlockLedgerConservationTests"/>: the pool is spent oldest-first
/// and the remainder the turn-start refill discards is charged newest-first,
/// so a source's generated total splits cleanly into what it bought and what
/// expired unspent.
/// </summary>
public class EnergyLedgerConservationTests
{
    [Fact]
    public void SpentThenDiscarded_ConservesEachCardsEnergy()
    {
        // A generates 2, then B generates 3 (ledger FIFO order: A, B).
        // 3 energy spent → A's 2 go first, then 1 of B's (FIFO).
        // 2 left in the pool at the refill → B's survivors wasted (LIFO).
        var pending = RunTracker.RunEnergyLedgerForTest(
            gains: new (string?, string?, int)[]
            {
                ("CARD.ADRENALINE#1", null, 2),
                ("CARD.ADRENALINE#2", null, 3),
            },
            spent: 3,
            leftoverDiscardedAtRefill: 2);

        var a = pending.CombatAggregates["CARD.ADRENALINE#1"];
        var b = pending.CombatAggregates["CARD.ADRENALINE#2"];

        Assert.Equal(2, a.TotalEnergyGenerated);
        Assert.Equal(0, a.TotalEnergyWasted);
        Assert.Equal(3, b.TotalEnergyGenerated);
        Assert.Equal(2, b.TotalEnergyWasted);

        // The invariant the pairing exists for: generated minus wasted is the
        // energy the card actually bought you.
        Assert.Equal(2, a.TotalEnergyGenerated - a.TotalEnergyWasted);
        Assert.Equal(1, b.TotalEnergyGenerated - b.TotalEnergyWasted);
    }

    [Fact]
    public void UntaggedRefill_AbsorbsWasteInsteadOfBlamingACard()
    {
        // The player's own turn refill enters untagged and, being oldest, is
        // spent first; the card's energy survives to the next spend rather
        // than being charged as waste.
        var pending = RunTracker.RunEnergyLedgerForTest(
            gains: new (string?, string?, int)[]
            {
                (null, null, 3),
                ("CARD.ADRENALINE#1", null, 2),
            },
            spent: 3,
            leftoverDiscardedAtRefill: 0);

        var card = pending.CombatAggregates["CARD.ADRENALINE#1"];
        Assert.Single(pending.CombatAggregates);
        Assert.Equal(2, card.TotalEnergyGenerated);
        Assert.Equal(0, card.TotalEnergyWasted);
    }

    [Fact]
    public void UntaggedRefillIsWastedLast_SoASpentCardIsNotBlamed()
    {
        // Refill 3 arrives first, then the card's 2. Nothing is spent and 5
        // expire: LIFO charges the card's 2 before the refill's 3, and the
        // untagged remainder credits nobody.
        var pending = RunTracker.RunEnergyLedgerForTest(
            gains: new (string?, string?, int)[]
            {
                (null, null, 3),
                ("CARD.ADRENALINE#1", null, 2),
            },
            spent: 0,
            leftoverDiscardedAtRefill: 5);

        var card = pending.CombatAggregates["CARD.ADRENALINE#1"];
        Assert.Single(pending.CombatAggregates);
        Assert.Equal(2, card.TotalEnergyWasted);
    }

    [Fact]
    public void RelicOwnedChunk_CreditsOnlyThatRelicsWastedEnergy()
    {
        var pending = RunTracker.RunEnergyLedgerForTest(
            gains: new (string?, string?, int)[]
            {
                (null, "RELIC.ART_OF_WAR", 1),
                ("CARD.ADRENALINE#1", null, 2),
            },
            spent: 1,
            leftoverDiscardedAtRefill: 2);

        Assert.Single(pending.CombatAggregates);
        var relic = pending.RelicAggregates["RELIC.ART_OF_WAR"];
        var card = pending.CombatAggregates["CARD.ADRENALINE#1"];

        // The relic's 1 is spent (FIFO), so all the waste is the card's.
        Assert.Equal(1, relic.EnergyGenerated);
        Assert.Equal(0, relic.EnergyWasted);
        Assert.Equal(2, card.TotalEnergyGenerated);
        Assert.Equal(2, card.TotalEnergyWasted);
    }
}
