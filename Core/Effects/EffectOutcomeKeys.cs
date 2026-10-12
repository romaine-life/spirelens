using System;
using System.Collections.Generic;

namespace SpireLens.Core.Effects;

/// <summary>
/// Persisted keys of the generic outcome bags (<c>RelicAggregate.Outcomes</c>,
/// <c>PowerAggregate.Outcomes</c>, <c>AppliedEffectAggregate.Outcomes</c>).
/// Keys are written verbatim into run files: never rename one, only add.
/// Every value is an observed outcome measured while the owner's own
/// callback was resolving, not the amount its text promises.
/// </summary>
internal static class EffectOutcomeKeys
{
    /// <summary>Owner callbacks that produced at least one outcome or a
    /// relic flash (relics), or produced an outcome (powers).</summary>
    public const string Triggers = "triggers";
    /// <summary>Combats the relic was held at combat start.</summary>
    public const string Combats = "combats";

    public const string Block = "block";
    public const string Damage = "damage";
    public const string DamageBlocked = "damage_blocked";
    public const string Overkill = "overkill";
    public const string Kills = "kills";
    public const string Hits = "hits";
    public const string HpLost = "hp_lost";
    public const string HpHealed = "hp_healed";
    public const string Energy = "energy";
    public const string Stars = "stars";
    public const string CardsDrawn = "cards_drawn";
    public const string HandDrawBonus = "hand_draw_bonus";
    public const string MaxEnergyBonus = "max_energy_bonus";
    public const string Gold = "gold";
    public const string MaxHp = "max_hp";
    public const string Forge = "forge";
    public const string OrbsChanneled = "orbs_channeled";
    public const string CardsGenerated = "cards_generated";
    public const string CardsUpgraded = "cards_upgraded";
    public const string CardsTransformed = "cards_transformed";
    public const string CardsRemoved = "cards_removed";
    public const string CardsAdded = "cards_added";
    public const string CardsDiscarded = "cards_discarded";
    public const string CardsRetrieved = "cards_retrieved";
    public const string CardsPutOnDrawPile = "cards_put_on_draw_pile";
    public const string CardsAutoPlayed = "cards_autoplayed";
    public const string OrbsEvoked = "orbs_evoked";
    public const string OrbPassivesTriggered = "orb_passives_triggered";

    private const string SelfPowerPrefix = "power:";
    private const string EnemyPowerPrefix = "debuff:";

    /// <summary>Stacks of a power the owner put on its own side.</summary>
    public static string SelfPower(string powerId) => SelfPowerPrefix + powerId;

    /// <summary>Stacks of a power the owner put on an enemy.</summary>
    public static string EnemyPower(string powerId) => EnemyPowerPrefix + powerId;

    public static bool TryParsePower(string key, out string powerId, out bool onEnemy)
    {
        if (key.StartsWith(SelfPowerPrefix, StringComparison.Ordinal))
        {
            powerId = key[SelfPowerPrefix.Length..];
            onEnemy = false;
            return powerId.Length > 0;
        }

        if (key.StartsWith(EnemyPowerPrefix, StringComparison.Ordinal))
        {
            powerId = key[EnemyPowerPrefix.Length..];
            onEnemy = true;
            return powerId.Length > 0;
        }

        powerId = "";
        onEnemy = false;
        return false;
    }

    /// <summary>Display order for the fixed keys. Power keys render after
    /// these, then any key this build does not know (kept, never dropped).</summary>
    public static readonly IReadOnlyList<string> DisplayOrder =
    [
        Triggers,
        Damage, DamageBlocked, Overkill, Kills, Hits,
        Block,
        HpHealed, HpLost, MaxHp,
        Energy, MaxEnergyBonus, Stars,
        CardsDrawn, HandDrawBonus,
        Forge, OrbsChanneled, OrbsEvoked, OrbPassivesTriggered,
        CardsGenerated, CardsUpgraded, CardsTransformed, CardsRemoved, CardsAdded,
        CardsDiscarded, CardsRetrieved, CardsPutOnDrawPile, CardsAutoPlayed,
        Gold,
    ];

    /// <summary>Keys whose per-combat average is worth showing when the
    /// combat denominator is known.</summary>
    public static bool IsCombatRateKey(string key) =>
        key is Damage or Block or HpHealed or Energy or Stars or CardsDrawn
            or HandDrawBonus or MaxEnergyBonus or Forge or CardsGenerated;

    /// <summary>Row label, phrased so <see cref="RelicStatRowVocabulary"/>
    /// can swap in its concept icons.</summary>
    public static string Label(string key) => key switch
    {
        Triggers => "Activations",
        Damage => "Damage dealt",
        DamageBlocked => "Damage blocked",
        Overkill => "Overkill",
        Kills => "Kills",
        Hits => "Targets hit",
        Block => "Block gained",
        HpHealed => "HP healed",
        HpLost => "HP lost",
        MaxHp => "Max HP changed",
        Energy => "Energy gained",
        MaxEnergyBonus => "Extra max Energy",
        Stars => "Stars gained",
        CardsDrawn => "Cards drawn",
        HandDrawBonus => "Extra cards in opening draws",
        Forge => "Forge granted",
        OrbsChanneled => "Orbs channeled",
        CardsGenerated => "Cards created",
        CardsUpgraded => "Cards upgraded",
        CardsTransformed => "Cards transformed",
        CardsRemoved => "Cards removed",
        CardsAdded => "Cards added to deck",
        CardsDiscarded => "Other cards discarded",
        CardsRetrieved => "Cards returned to hand",
        CardsPutOnDrawPile => "Cards put on draw pile",
        CardsAutoPlayed => "Other cards auto-played",
        OrbsEvoked => "Orbs evoked",
        OrbPassivesTriggered => "Orb passives triggered",
        Gold => "Gold gained",
        _ => key,
    };
}
