using SpireLens.Core.Replay.Playback;
using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>
/// Pins the playlist file contract shared with the run-database web page
/// (docs/replay-player.md, "Playlists").
/// </summary>
public class PlaylistFormatTests
{
    [Fact]
    public void ParsePlaylist_ReadsNameRunsAndIgnoresUnknownFields()
    {
        const string json = """
        {"format_version":1,"name":"Regent wins","created_at_utc":"2026-10-16T01:00:00Z",
         "updated_at_utc":"2026-10-16T02:00:00Z","filter":{"character":"REGENT"},"extra":[1,2],
         "runs":[{"game_start_time":1792120868,"label":"A10 win","note":"x"},
                 {"game_start_time":1791852118},
                 {"game_start_time":"bad"},{"label":"no id"},7]}
        """;

        var playlist = PlaylistFormat.ParsePlaylist(json, "regent-wins", out var error);

        Assert.Null(error);
        Assert.NotNull(playlist);
        Assert.Equal("regent-wins", playlist!.Slug);
        Assert.Equal("Regent wins", playlist.Name);
        Assert.Equal("2026-10-16T02:00:00Z", playlist.UpdatedAtUtc);
        Assert.Equal(2, playlist.Runs.Count);
        Assert.Equal(new PlaylistRunRef(1792120868, "A10 win"), playlist.Runs[0]);
        Assert.Equal(new PlaylistRunRef(1791852118, null), playlist.Runs[1]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"name\":\"x\"}")]
    [InlineData("{\"format_version\":0,\"runs\":[]}")]
    public void ParsePlaylist_RejectsUnreadableFiles(string json)
    {
        Assert.Null(PlaylistFormat.ParsePlaylist(json, "x", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ParsePlaylist_DefaultsTheNameToTheSlug()
    {
        var playlist = PlaylistFormat.ParsePlaylist("{\"runs\":[]}", "my-list", out _);
        Assert.Equal("my-list", playlist!.Name);
        Assert.Empty(playlist.Runs);
    }

    [Fact]
    public void ParsePlayNow_AcceptsASlugOrInlineRuns()
    {
        var bySlug = PlaylistFormat.ParsePlayNow(
            "{\"format_version\":1,\"requested_at_utc\":\"2026-10-16T03:00:00Z\",\"playlist\":\"regent-wins\"}", out _);
        Assert.Equal("regent-wins", bySlug!.PlaylistSlug);
        Assert.Null(bySlug.Runs);
        Assert.Equal("2026-10-16T03:00:00Z", bySlug.RequestedAtUtc);

        var inline = PlaylistFormat.ParsePlayNow(
            "{\"format_version\":1,\"runs\":[{\"game_start_time\":5,\"label\":\"five\"}]}", out _);
        Assert.Null(inline!.PlaylistSlug);
        Assert.Equal(new PlaylistRunRef(5, "five"), Assert.Single(inline.Runs!));

        Assert.Null(PlaylistFormat.ParsePlayNow("{\"format_version\":1}", out var error));
        Assert.NotNull(error);
        Assert.Null(PlaylistFormat.ParsePlayNow("{\"playlist\":\"\"}", out _));
    }

    [Theory]
    [InlineData("regent-wins.json", "regent-wins")]
    [InlineData("play-now.json", null)]
    [InlineData("PLAY-NOW.JSON", null)]
    [InlineData("notes.txt", null)]
    [InlineData(".json", null)]
    public void SlugFromFileName_SkipsTheRequestFileAndNonJson(string file, string? slug)
    {
        Assert.Equal(slug, PlaylistFormat.SlugFromFileName(file));
    }

    [Theory]
    [InlineData("regent-wins", true)]
    [InlineData("../progress", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("C:x", false)]
    [InlineData("", false)]
    public void IsSafeSlug_RefusesPaths(string slug, bool safe)
    {
        Assert.Equal(safe, PlaylistFormat.IsSafeSlug(slug));
    }
}
