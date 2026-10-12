using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace SpireLens.Core.Effects;

/// <summary>
/// The relics and powers whose stats come from the generic effect-frame
/// framework instead of a hand-written patch. Enrolling one is one line here:
/// its own async hook overrides are discovered by reflection and framed by
/// <see cref="Patches.EffectFrameTaskPatch"/>, and the existing outcome
/// observers credit whatever those hooks observably did.
///
/// Rules for enrolling:
///   - never enroll a relic or power that already has bespoke tracking; its
///     hand-written windows and the frame would both claim the same outcome;
///   - names are resolved in the game assembly itself at patch
///     time, never through ModelDb, which may not exist yet on a cold start;
///   - a name the current game no longer has is skipped, not an error.
/// </summary>
internal static class EffectRegistry
{
    private const string RelicNamespace = "MegaCrit.Sts2.Core.Models.Relics.";
    private const string PowerNamespace = "MegaCrit.Sts2.Core.Models.Powers.";

    /// <summary>Relics the 2026-10 coverage audit found with no stats.</summary>
    internal static readonly string[] RelicClassNames =
    [
        // Combat: block, damage, healing, self-damage.
        "DiamondDiadem", "Sai", "FakeOrichalcum", "ToughBandages", "GalacticDust",
        "SelfFormingClay", "PaelsLegion", "BlackBlood", "DemonTongue", "CharonsAshes",
        "Tingsha", "Metronome", "StoneCalendar", "RoyalPoison", "TheBoot",
        "VitruvianMinion", "UndyingSigil", "PaperKrane",
        // Combat: powers applied.
        "HandDrill", "TwistedFunnel", "SneckoSkull", "PhilosophersStone", "Brimstone",
        "SlingOfCourage", "MiniRegent", "DataDisk", "HelicalDart", "BeltBuckle",
        // Combat: energy, stars, forge, orbs.
        "Bread", "Ectoplasm", "Sozu", "BlessedAntler", "VelvetChoker", "PaelsFlesh",
        "PaelsTears", "IvoryTile", "DivineRight", "DivineDestiny", "LunarPastry",
        "FencingManual", "EmotionChip", "ChemicalX", "RunicCapacitor",
        // Combat: draw, hand, generated cards, upgrades.
        "RingOfTheDrake", "PaelsBlood", "SneckoEye", "FakeSneckoEye", "Fiddle",
        "BigMushroom", "UnceasingTop", "PowerCell", "JeweledMask", "RunicPyramid",
        "RingingTriangle", "BigHat", "OrangeDough", "VexingPuzzlebox", "NinjaScroll",
        "FuneraryMask", "RadiantPearl", "TeaOfDiscourtesy", "Bellows", "BoneTea",
        "HistoryCourse", "GhostSeed", "FurCoat",
        // Pickup: deck changes, max HP, gold, rewards.
        "BiiigHug", "NeowsTalisman", "Pomander", "Astrolabe", "NewLeaf", "PandorasBox",
        "Claws", "ArchaicTooth", "EmptyCage", "PreciseScissors", "PreservedFog",
        "BeautifulBracelet", "ElectricShrymp", "Kifuda", "PunchDagger", "RoyalStamp",
        "PaelsGrowth", "DollysMirror", "DustyTome", "JewelryBox", "NeowsTorment",
        "TanxsWhistle", "PaelsHorn", "SereTalon", "DistinguishedCape", "NeowsSacrifice",
        "CallingBell", "Byrdpip", "LoomingFruit", "DragonFruit", "GlassEye",
        "LostCoffer", "Kaleidoscope", "MassiveScroll", "SeaGlass", "Cauldron",
        "DelicateFrond", "Glitter", "LavaLamp", "Driftwood", "BlackStar", "LavaRock",
        "DreamCatcher", "MeatCleaver", "TheCourier", "GoldenCompass", "LordsParasol",
        "MiniatureTent",
    ];

    /// <summary>Player powers whose payoff is a command their own hooks
    /// issue. A power migrated off bespoke tracking must stop writing its
    /// legacy fields in the same change that enrolls it. Damage/block modifiers (Strength, Vulnerable...) are not
    /// here: their marginal contribution needs its own convention. Thorns is
    /// not here because Bronze Scales already owns a Thorns damage window.</summary>
    internal static readonly string[] PowerClassNames =
    [
        // Damage dealt.
        "BlackHolePower", "CacophonyPower", "FlameBarrierPower", "HailstormPower",
        "HauntPower", "InfernoPower", "JuggernautPower", "NecroMasteryPower",
        "ReflectPower", "RollingBoulderPower", "SerpentFormPower", "SleightOfFleshPower",
        "SmokestackPower", "SpeedsterPower", "StranglePower", "TheBombPower",
        "ThunderPower", "PhantomBladesPower", "CrimsonMantlePower", "DemisePower",
        "TheGambitPower",
        // Block.
        "AfterimagePower", "BeaconOfHopePower", "BlockNextTurnPower", "ChildOfTheStarsPower",
        "CoolantPower", "PillarOfCreationPower", "PlatingPower", "RagePower",
        "SelfFormingClayPower", "ShroudPower", "SneakyPower", "SpiritOfAshPower",
        "ToricToughnessPower",
        // Draw and energy.
        "IterationPower", "PagestormPower", "DrawCardsNextTurnPower", "DemesnePower",
        "MachineLearningPower", "ToolsOfTheTradePower", "TyrannyPower", "ClarityPower",
        "AutomationPower", "EnergyNextTurnPower", "OrbitPower", "RadiancePower",
        "SubroutinePower", "GenesisPower", "StarNextTurnPower", "TheSealedThronePower",
        "FriendshipPower", "PyrePower",
        // Stacks of another power.
        "ArsenalPower", "DemonFormPower", "MonologuePower", "RitualPower",
        "WraithFormPower", "BiasedCognitionPower", "PrepTimePower", "CountdownPower",
        "NeurosurgePower", "ReaperFormPower", "UnderworldPower", "OblivionPower",
        "EnvenomPower", "ConcoctPower", "CorrosiveWavePower", "MonarchsGazePower",
        "TankPower",
        // Cards, forge, sustain, gold.
        "SentryModePower", "NightmarePower", "SoulboundPower", "ForegoneConclusionPower",
        "StratagemPower", "InfiniteBladesPower", "ImprovementPower", "FurnacePower",
        "HammerTimePower", "RegenPower", "RoyaltiesPower",
        // Migrated from bespoke windows (their frozen legacy PowerAggregate
        // fields are still read and added to the bag by the tooltip).
        "FeelNoPainPower", "DanseMacabrePower", "DarkEmbracePower", "ViciousPower",
    ];

    // Resolved against the game assembly that defines RelicModel/PowerModel,
    // never by scanning every loaded assembly: AccessTools.TypeByName's scan
    // can run another assembly's module initializer, which threw in the test
    // host, and Lazy<T> would then cache that exception for the process.
    private static readonly Lazy<HashSet<Type>> RelicTypes =
        new(() => ResolveTypes(typeof(RelicModel).Assembly, RelicNamespace, RelicClassNames));

    private static readonly Lazy<HashSet<Type>> PowerTypes =
        new(() => ResolveTypes(typeof(PowerModel).Assembly, PowerNamespace, PowerClassNames));

    public static bool IsEnrolledRelic(RelicModel? relic)
        => relic != null && RelicTypes.Value.Contains(relic.GetType());

    public static bool IsEnrolledPower(PowerModel? power)
        => power != null && PowerTypes.Value.Contains(power.GetType());

    public static bool IsEnrolled(AbstractModel? model) => model switch
    {
        RelicModel relic => IsEnrolledRelic(relic),
        PowerModel power => IsEnrolledPower(power),
        _ => false,
    };

    public static IEnumerable<Type> EnrolledTypes()
        => RelicTypes.Value.Concat(PowerTypes.Value);

    /// <summary>Every async hook override an enrolled type declares itself.
    /// Only overrides: a hook the type does not override never runs its code,
    /// so there is nothing of its own to frame.</summary>
    public static IEnumerable<MethodBase> TaskHookMethods()
    {
        foreach (var type in EnrolledTypes())
        {
            foreach (var method in type.GetMethods(
                         BindingFlags.Instance | BindingFlags.Public
                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                if (method.ReturnType != typeof(Task)) continue;
                if (!IsOverride(method)) continue;
                yield return method;
            }
        }
    }

    /// <summary>Overrides of a decimal modifier hook (for example
    /// <c>ModifyHandDraw</c>) declared by enrolled types.</summary>
    public static IEnumerable<MethodBase> DecimalModifierMethods(params string[] names)
    {
        foreach (var type in EnrolledTypes())
        {
            foreach (var name in names)
            {
                var method = type.GetMethod(
                    name,
                    BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (method == null || method.ReturnType != typeof(decimal)) continue;
                if (!IsOverride(method)) continue;
                yield return method;
            }
        }
    }

    /// <summary>Whether this enrolled model overrides the named hook.</summary>
    public static bool Overrides(AbstractModel model, string name)
    {
        var method = model.GetType().GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return method != null && method.DeclaringType != typeof(AbstractModel) && IsOverride(method);
    }

    private static bool IsOverride(MethodInfo method)
    {
        if (!method.IsVirtual) return false;
        var baseDefinition = method.GetBaseDefinition();
        return baseDefinition.DeclaringType != method.DeclaringType;
    }

    private static HashSet<Type> ResolveTypes(Assembly gameAssembly, string ns, IEnumerable<string> names)
    {
        var result = new HashSet<Type>();
        foreach (var name in names)
        {
            Type? type = null;
            try
            {
                type = gameAssembly.GetType(ns + name, throwOnError: false);
            }
            catch
            {
                // A single unresolvable name must not take the registry down.
            }

            if (type == null)
            {
                try { CoreMain.LogDebug($"EffectRegistry: {ns}{name} not found in this game build; skipped"); }
                catch { }
                continue;
            }
            result.Add(type);
        }
        return result;
    }
}
