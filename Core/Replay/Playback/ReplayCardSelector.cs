using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// The game's own card-selection seam (<c>CardSelectCmd.LocalSelector</c>,
/// installed with <c>CardSelectCmd.PushSelector(selector, localOnly: true)</c>).
/// Every <c>CardSelectCmd</c> path that would show the local player a card
/// selection (hand, combat pile, choose-a-card, simple grid, deck upgrade /
/// transform / enchant / removal / generic) asks it instead of opening the
/// screen, after reserving the choice id exactly as it would for the screen.
/// The request waits until the playback driver reaches the recorded choice
/// and completes it with the recorded cards.
/// </summary>
internal sealed class ReplayCardSelector : ICardSelector
{
    internal sealed class Request
    {
        public IReadOnlyList<CardModel> Options = new List<CardModel>();
        public int MinSelect;
        public int MaxSelect;
        public readonly TaskCompletionSource<IEnumerable<CardModel>> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Request? Pending { get; private set; }

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        var request = new Request { Options = options.ToList(), MinSelect = minSelect, MaxSelect = maxSelect };
        Pending = request;
        return request.Completion.Task;
    }

    /// <summary>Only used by the game when no card reward screen can be shown; playback drives the screen instead.</summary>
    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives) => default;

    public void Complete(IEnumerable<CardModel> picked)
    {
        var request = Pending;
        Pending = null;
        request?.Completion.TrySetResult(picked.ToList());
    }

    /// <summary>
    /// Release a waiting game flow without a pick (the run is being torn down
    /// for a resync or the end of playback). An empty selection is something
    /// every caller handles; a cancelled task would surface as a game error.
    /// </summary>
    public void Abandon()
    {
        var request = Pending;
        Pending = null;
        request?.Completion.TrySetResult(new List<CardModel>());
    }
}
