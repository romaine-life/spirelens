using SpireLens.Core;
using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>
/// Pins forge attribution, the third of the provenance ledgers alongside
/// <see cref="BlockLedgerConservationTests"/> and
/// <see cref="EnergyLedgerConservationTests"/>. Forge from several cards piles
/// onto one shared Sovereign Blade, and it dies with the combat, so the
/// question each test answers is whether a card's forge ever got swung.
/// </summary>
public class ForgeLedgerConservationTests
{
    [Fact]
    public void BladeSwung_LeavesNoForgeWasted()
    {
        // The Blade's damage IS the accumulated forge, so playing it cashes in
        // every outstanding chunk at once — no ordering to settle.
        var pending = RunTracker.RunForgeLedgerForTest(
            grants: new (string, decimal)[]
            {
                ("CARD.TEMPER#1", 3m),
                ("CARD.TEMPER#2", 4m),
            },
            bladePlayed: true);

        var a = pending.CombatAggregates["CARD.TEMPER#1"];
        var b = pending.CombatAggregates["CARD.TEMPER#2"];

        Assert.Equal(3m, a.TotalForgeGenerated);
        Assert.Equal(0m, a.TotalForgeWasted);
        Assert.Equal(4m, b.TotalForgeGenerated);
        Assert.Equal(0m, b.TotalForgeWasted);
    }

    [Fact]
    public void BladeNeverSwung_ChargesEveryCardItsOwnForge()
    {
        // Combat ended with the Blade still in hand. Each card is charged
        // exactly what it granted — not a share of some pooled total.
        var pending = RunTracker.RunForgeLedgerForTest(
            grants: new (string, decimal)[]
            {
                ("CARD.TEMPER#1", 3m),
                ("CARD.TEMPER#2", 4m),
            },
            bladePlayed: false);

        var a = pending.CombatAggregates["CARD.TEMPER#1"];
        var b = pending.CombatAggregates["CARD.TEMPER#2"];

        Assert.Equal(3m, a.TotalForgeWasted);
        Assert.Equal(4m, b.TotalForgeWasted);
        Assert.Equal(a.TotalForgeGenerated, a.TotalForgeWasted);
        Assert.Equal(b.TotalForgeGenerated, b.TotalForgeWasted);
    }

    [Fact]
    public void FractionalForge_KeepsItsDecimalShapeThroughTheLedger()
    {
        // Forge is decimal-backed in the game's command path even though most
        // current cards use whole numbers, so the wasted half must not round.
        var pending = RunTracker.RunForgeLedgerForTest(
            grants: new (string, decimal)[] { ("CARD.TEMPER#1", 2.5m) },
            bladePlayed: false);

        Assert.Equal(2.5m, pending.CombatAggregates["CARD.TEMPER#1"].TotalForgeWasted);
    }
}
