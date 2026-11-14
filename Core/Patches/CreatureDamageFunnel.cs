using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace SpireLens.Core.Patches;

/// <summary>
/// The <c>CreatureCmd.Damage</c> overload every multi-target damage call ends
/// in. STS2 v0.113 added an overload taking <c>IEnumerable&lt;DamageTargetData&gt;</c>:
/// card attacks (<c>AttackCommand</c>) call it directly and the creature-list
/// overload forwards to it. Earlier builds end in the creature-list overload.
/// Both keep the parameter names the patches bind (<c>choiceContext</c>,
/// <c>amount</c>, <c>dealer</c>, <c>cardSource</c>, <c>cardPlay</c>).
/// </summary>
internal static class CreatureDamageFunnel
{
    internal static readonly MethodInfo? Target =
        AccessTools.GetDeclaredMethods(typeof(CreatureCmd)).FirstOrDefault(IsTargetDataOverload)
        ?? AccessTools.DeclaredMethod(
            typeof(CreatureCmd),
            nameof(CreatureCmd.Damage),
            new[]
            {
                typeof(PlayerChoiceContext),
                typeof(IEnumerable<Creature>),
                typeof(decimal),
                typeof(ValueProp),
                typeof(Creature),
                typeof(CardModel),
                typeof(CardPlay),
            });

    private static bool IsTargetDataOverload(MethodInfo method)
    {
        if (method.Name != nameof(CreatureCmd.Damage)) return false;
        var parameters = method.GetParameters();
        if (parameters.Length < 2) return false;
        var targets = parameters[1].ParameterType;
        return targets.IsGenericType
            && targets.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            && targets.GetGenericArguments()[0].Name == "DamageTargetData";
    }
}
