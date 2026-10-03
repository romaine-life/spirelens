using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.sts2.Core.Nodes.TopBar;

namespace SpireLens.Core.Patches;

/// <summary>
/// Appends the current combat's round-by-round damage dealt and taken to the
/// native top-bar room-icon tooltip. The game shows that tooltip with the
/// icon's inner <c>Icon</c> TextureRect as owner, not the
/// <see cref="NTopBarRoomIcon"/> itself.
/// </summary>
internal static class EncounterStatsTooltip
{
    internal static bool IsRoomIconTipOwner(Control owner)
        => owner is TextureRect && owner.GetParent() is NTopBarRoomIcon;

    internal static bool TryBuildNativeHoverTip(out HoverTip tip)
    {
        tip = default;

        var combatManager = CombatManager.Instance;
        int liveRound = combatManager?.IsInProgress == true
            ? combatManager.DebugOnlyGetState()?.RoundNumber ?? 0
            : 0;

        if (!EncounterTurnDamage.TryGetForCurrentRoom(
                RunManager.Instance?.State,
                liveRound,
                out var rounds))
        {
            return false;
        }

        tip = StatsTooltip.CreateNativeTip(
            "Combat damage",
            BuildBodyBBCode(rounds));
        return true;
    }

    internal static string BuildBodyBBCode(IReadOnlyList<EncounterTurnDamage.RoundDamage> rounds)
    {
        var body = new StringBuilder();
        body.Append("[table=3]");
        AppendHeaderCell(body, StatConceptGlossary.RenderHintedGlyph("turn"), first: true);
        AppendHeaderCell(body, StatConceptGlossary.RenderHintedGlyph("damage") + " Dealt", first: false);
        AppendHeaderCell(body, StatConceptGlossary.RenderHintedGlyph("hp") + " Taken", first: false);

        int totalDealt = 0;
        int totalTaken = 0;
        foreach (var round in rounds)
        {
            totalDealt += round.Dealt;
            totalTaken += round.Taken;
            AppendValueCell(body, Format(round.Round), first: true, bold: false);
            AppendValueCell(body, Format(round.Dealt), first: false, bold: false);
            AppendValueCell(body, Format(round.Taken), first: false, bold: false);
        }

        AppendValueCell(body, "Total", first: true, bold: true);
        AppendValueCell(body, Format(totalDealt), first: false, bold: true);
        AppendValueCell(body, Format(totalTaken), first: false, bold: true);
        body.Append("[/table]");
        return body.ToString();
    }

    private static void AppendHeaderCell(StringBuilder body, string content, bool first)
    {
        body.Append(first
                ? "[cell expand=0 padding=0,0,16,4]"
                : "[cell expand=0 padding=0,0,16,4][right]")
            .Append(content)
            .Append(first ? "[/cell]" : "[/right][/cell]");
    }

    private static void AppendValueCell(StringBuilder body, string value, bool first, bool bold)
    {
        body.Append(first
            ? "[cell expand=0 padding=0,0,16,0]"
            : "[cell expand=0 padding=0,0,16,0][right]");
        if (bold) body.Append("[b]");
        body.Append(value);
        if (bold) body.Append("[/b]");
        body.Append(first ? "[/cell]" : "[/right][/cell]");
    }

    private static string Format(int value)
        => value.ToString(CultureInfo.InvariantCulture);
}
