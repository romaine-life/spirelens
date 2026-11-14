using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;

namespace SpireLens.Core;

/// <summary>
/// Creates SpireLens statistics as ordinary game hover-tip data.
///
/// SpireLens does not own a panel, scene-tree node, anchor, or hover lifecycle.
/// The game's NHoverTipSet receives the returned HoverTip together with its
/// native tips and therefore owns creation, positioning, and removal.
/// </summary>
public static class StatsTooltip
{
    private const string InlineStatTableOpen = "[table=3]";
    private const string InlineStatTableClose = "[/table]";
    private const string ScalarStatTableOpen = "[table=4]";
    private const string ScalarStatTableClose = "[/table]\n";

    private const string NativeStatsTipId = "SPIRELENS.STATS";
    private const string NativeHintTipId = "SPIRELENS.HINT";
    private const int BodyFontSize = 20;

    // Id is an auto-property; v0.113 dropped its setter, so write the backing field.
    private static readonly FieldInfo IdField =
        AccessTools.Field(typeof(HoverTip), "<Id>k__BackingField");

    private static readonly PropertyInfo TitleProperty =
        AccessTools.Property(typeof(HoverTip), nameof(HoverTip.Title));

    private static readonly PropertyInfo DescriptionProperty =
        AccessTools.Property(typeof(HoverTip), nameof(HoverTip.Description));

    internal static HoverTip CreateNativeTip(
        string titleText,
        string bodyBBCode,
        bool stretchHorizontally = false)
    {
        var tip = new HoverTip
        {
            ShouldOverrideTextOverflow = stretchHorizontally,
        };

        // HoverTip is a record struct whose raw-string Title and Description
        // setters are private. Box it so the same native fields used by the
        // game can be populated without inventing a parallel UI node.
        object boxed = tip;
        IdField.SetValue(boxed, NativeStatsTipId);
        TitleProperty.SetValue(boxed, titleText);
        DescriptionProperty.SetValue(
            boxed,
            $"[font_size={BodyFontSize}]{bodyBBCode}[/font_size]");
        return (HoverTip)boxed;
    }

    internal static HoverTip CreateNativeHint(string bodyText)
    {
        // Leave Title null so the native scene omits its header entirely.
        object boxed = new HoverTip();
        IdField.SetValue(boxed, NativeHintTipId);
        DescriptionProperty.SetValue(
            boxed,
            $"[left][font_size={BodyFontSize}]{EscapeBbcode(bodyText)}[/font_size][/left]");
        return (HoverTip)boxed;
    }

    internal static string RenderRowInformationHint(
        string label,
        string? fullDescription = null)
    {
        var description = RelicStatRowVocabulary.Create(
            label,
            fullDescription).FullDescription;
        return StatConceptGlossary.RenderInformationHint(description);
    }

    internal static RelicStatRowPresentation CreateStatRowPresentation(
        string label,
        string? fullDescription = null,
        IReadOnlyList<string>? conceptIds = null,
        IReadOnlyList<string>? denominatorConceptIds = null)
    {
        var inferred = RelicStatRowVocabulary.Create(label, fullDescription);
        return RelicStatRowVocabulary.MergeConcepts(
            inferred,
            conceptIds ?? Array.Empty<string>(),
            denominatorConceptIds ?? Array.Empty<string>());
    }

    internal static void AppendConceptLabel(
        StringBuilder body,
        IReadOnlyList<string> conceptIds,
        IReadOnlyList<string> denominatorConceptIds,
        string label)
    {
        for (var index = 0; index < conceptIds.Count; index++)
        {
            if (index > 0) body.Append(' ');
            if (denominatorConceptIds.Contains(
                    conceptIds[index],
                    StringComparer.Ordinal))
            {
                body.Append(StatConceptGlossary.RenderHintedGlyph("per"))
                    .Append(' ');
            }
            body.Append(StatConceptGlossary.RenderHintedGlyph(conceptIds[index]));
        }

        if (string.IsNullOrWhiteSpace(label)) return;

        if (conceptIds.Count > 0) body.Append(' ');
        body.Append(label);
    }

    internal static void AppendInlineStatRow(
        StringBuilder body,
        IReadOnlyList<string> conceptIds,
        IReadOnlyList<string> denominatorConceptIds,
        string label,
        string value,
        string fullDescription)
    {
        var presentation = CreateStatRowPresentation(
            label,
            fullDescription,
            conceptIds,
            denominatorConceptIds);
        BeginOrContinueInlineStatTable(body);
        body.Append("[cell expand=0 padding=0,0,8,0]")
            .Append(StatConceptGlossary.RenderInformationHint(
                presentation.FullDescription))
            .Append("[/cell]")
            .Append("[cell expand=0 padding=0,0,12,0]");
        AppendConceptLabel(
            body,
            presentation.ConceptIds,
            presentation.DenominatorConceptIds,
            presentation.Label);
        body.Append("[/cell]")
            .Append("[cell expand=0 padding=0,0,0,0][left][b]")
            .Append(value)
            .Append("[/b][/left][/cell]")
            .Append(InlineStatTableClose);
    }

    /// <summary>
    /// Appends a scalar row to the canonical natural-width stats table.
    /// Consecutive rows share one table, so the widest semantic label sets a
    /// single left edge for every value (and the widest value does not push
    /// shorter values sideways).
    /// </summary>
    internal static void AppendScalarStatRow(
        StringBuilder body,
        RelicStatRowPresentation presentation,
        string value,
        string pct = "",
        string? labelColor = null)
    {
        BeginOrContinueScalarStatTable(body);
        body.Append("[cell expand=0 padding=0,0,10,0]")
            .Append(StatConceptGlossary.RenderInformationHint(
                presentation.FullDescription))
            .Append("[/cell]")
            .Append("[cell expand=0 padding=0,0,12,0]");
        if (!string.IsNullOrEmpty(labelColor))
            body.Append("[color=").Append(labelColor).Append(']');
        AppendConceptLabel(
            body,
            presentation.ConceptIds,
            presentation.DenominatorConceptIds,
            presentation.Label);
        if (!string.IsNullOrEmpty(labelColor))
            body.Append("[/color]");
        body.Append("[/cell]")
            .Append("[cell expand=0 padding=0,0,12,0][left][b]")
            .Append(value)
            .Append("[/b][/left][/cell]")
            .Append("[cell expand=0 padding=0,0,4,0][left][color=#b5b5b5]")
            .Append(pct)
            .Append("[/color][/left][/cell]")
            .Append(ScalarStatTableClose);
    }

    internal static bool ContainsScalarStatTable(string? body)
        => !string.IsNullOrEmpty(body)
           && body.Contains(ScalarStatTableOpen, StringComparison.Ordinal);

    private static void BeginOrContinueScalarStatTable(StringBuilder body)
    {
        if (EndsWith(body, ScalarStatTableClose)
            && LastTableStartsWith(body, ScalarStatTableOpen))
        {
            body.Length -= ScalarStatTableClose.Length;
            return;
        }

        body.Append(ScalarStatTableOpen);
    }

    private static void BeginOrContinueInlineStatTable(StringBuilder body)
    {
        if (body.Length == 0)
        {
            body.Append(InlineStatTableOpen);
            return;
        }

        if (EndsWith(body, InlineStatTableClose))
        {
            body.Length -= InlineStatTableClose.Length;
            body.Append('\n');
            return;
        }

        body.Append('\n').Append(InlineStatTableOpen);
    }

    private static bool EndsWith(StringBuilder body, string suffix)
    {
        if (body.Length < suffix.Length) return false;

        var start = body.Length - suffix.Length;
        for (var index = 0; index < suffix.Length; index++)
        {
            if (body[start + index] != suffix[index]) return false;
        }

        return true;
    }

    private static bool LastTableStartsWith(StringBuilder body, string tableOpen)
    {
        for (var start = body.Length - tableOpen.Length; start >= 0; start--)
        {
            if (MatchesAt(body, start, "[table="))
                return MatchesAt(body, start, tableOpen);
        }

        return false;
    }

    private static bool MatchesAt(StringBuilder body, int start, string value)
    {
        if (start < 0 || start + value.Length > body.Length) return false;

        for (var index = 0; index < value.Length; index++)
        {
            if (body[start + index] != value[index]) return false;
        }

        return true;
    }

    /// <summary>
    /// Escape a dynamic string for safe inclusion in native hover-tip BBCode.
    /// Godot renders "[lb]" as a literal opening bracket.
    /// </summary>
    public static string EscapeBbcode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        return text.Replace("[", "[lb]", StringComparison.Ordinal);
    }
}
