using System.Text.Json;
using LogExplorer;

namespace LogExplorer.Tests;

public class HashtagExtractorTests
{
    [Fact]
    public void ExtractsPlainAndUnicodeHashtags()
    {
        var tags = HashtagExtractor.Extract("New role! #VoiceActor #黄泉のツガイ #Δ帽子");
        Assert.Equal(new[] { "VoiceActor", "黄泉のツガイ", "Δ帽子" }, tags);
    }

    [Fact]
    public void KeepsHashtagsFromMarkdownLinkLabels()
    {
        var tags = HashtagExtractor.Extract("[#AX2026](<https://twitter.com/hashtag/AX2026>) hype!");
        Assert.Equal(new[] { "AX2026" }, tags);
    }

    [Fact]
    public void IgnoresUrlFragmentsAndBareUrls()
    {
        var tags = HashtagExtractor.Extract(
            "look https://bsky.app/profile/x/post/abc#repostId=123 and https://ex.com/page#section");
        Assert.Empty(tags);
    }

    [Fact]
    public void IgnoresPureNumbersAndDiscordMarkup()
    {
        var tags = HashtagExtractor.Extract("we are #1 fans <:bsky:1303506117032280087> <t:1782985063:f> #really");
        Assert.Equal(new[] { "really" }, tags);
    }

    [Fact]
    public void DeduplicatesCaseInsensitively()
    {
        var tags = HashtagExtractor.Extract("#Anime stuff #anime more", "#ANIME");
        Assert.Equal(new[] { "Anime" }, tags);
    }
}

public class TweetEmbedParserTests
{
    private static JsonElement Embed(string json) => JsonDocument.Parse(json).RootElement;

    private static readonly DateTimeOffset MsgTs = DateTimeOffset.Parse("2026-07-01T06:13:24+00:00");

    [Fact]
    public void ParsesPlainTweet()
    {
        var post = TweetEmbedParser.Parse(Embed("""
        {
          "url": "https://twitter.com/pheberryfab/status/2072201647657271677",
          "timestamp": "2026-07-01T06:12:27+00:00",
          "description": "So cool! #VoiceActor",
          "author": { "name": "Phebe Fabacher 🍓 (@pheberryfab)" }
        }
        """), MsgTs, "f.json", "general-x-twitter-posts");

        Assert.NotNull(post);
        Assert.Equal(Platform.Twitter, post!.Platform);
        Assert.Equal("pheberryfab", post.Handle);
        Assert.Equal("Phebe Fabacher 🍓", post.DisplayName);
        Assert.Null(post.RepostedBy);
        Assert.Equal(new[] { "VoiceActor" }, post.Hashtags);
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T06:12:27+00:00"), post.Timestamp);
    }

    [Fact]
    public void DetectsRetweetWhenUrlHandleDiffersFromAuthor()
    {
        var post = TweetEmbedParser.Parse(Embed("""
        {
          "url": "https://twitter.com/VAMichaelaLaws/status/2071496977750089873",
          "timestamp": "2026-06-29T07:28:22+00:00",
          "description": "Careful.",
          "author": { "name": "Vantacrow Bringer🃏🔪 NIJISANJI EN (@Tyrant_Vanta) ✧" }
        }
        """), MsgTs, "f.json", "c");

        Assert.Equal("Tyrant_Vanta", post!.Handle);
        Assert.Equal("VAMichaelaLaws", post.RepostedBy);
    }

    [Fact]
    public void CanonicalizesXComUrls()
    {
        var post = TweetEmbedParser.Parse(Embed("""
        { "url": "https://x.com/SomeUser/status/123456", "description": "" }
        """), MsgTs, "f.json", "c");
        Assert.Equal("https://twitter.com/SomeUser/status/123456", post!.Url);
    }

    [Fact]
    public void FallsBackToMessageTimestampForDegenerateEmbeds()
    {
        // Deleted tweets produce embeds like: timestamp 0001-01-01, author "(@)".
        var post = TweetEmbedParser.Parse(Embed("""
        {
          "url": "https://twitter.com/Ghost/status/9",
          "timestamp": "0001-01-01T00:00:00+00:00",
          "description": "",
          "author": { "name": "(@)" }
        }
        """), MsgTs, "f.json", "c");
        Assert.Equal(MsgTs, post!.Timestamp);
        Assert.Equal("Ghost", post.Handle);
    }

    [Fact]
    public void RejectsNonStatusUrls()
    {
        Assert.False(TweetEmbedParser.IsStatusUrl("https://pbs.twimg.com/media/x.jpg"));
        Assert.True(TweetEmbedParser.IsStatusUrl("https://twitter.com/a/status/1"));
    }
}

public class BlueskyComponentParserTests
{
    private static readonly DateTimeOffset MsgTs = DateTimeOffset.Parse("2026-07-02T09:37:45+00:00");

    private const string RepostMessage = """
    {
      "timestamp": "2026-07-02T09:37:45.719000+00:00",
      "components": [
        { "type": 10, "id": 1, "content": "**ghaspey.bsky.social** Just Posted On Bluesky" },
        { "type": 17, "id": 2, "components": [
            { "type": 9, "id": 3, "components": [
                { "type": 10, "id": 4, "content": "### <:bsky_repost:1380198177050726542> - [**ryan cooper (@ryanlcooper.com)**](https://bsky.app/profile/did:plc:p5/post/3mpm2ogddt22w#repostId=3mpnqbp6om72k)" },
                { "type": 10, "id": 5, "content": "it is a gross injustice #Politics" }
            ]},
            { "type": 10, "id": 8, "content": "**Quoting**\n[**Sharon (@sharonk.bsky.social)**](https://bsky.app/profile/sharonk.bsky.social)" },
            { "type": 10, "id": 9, "content": "so much of discourse #Discourse" },
            { "type": 10, "id": 10, "content": "-# Reposted By ghaspey.bsky.social" },
            { "type": 10, "id": 11, "content": "-# <:bsky:1303506117032280087>  Bluesky  •  <t:1782985063:f>" }
        ]}
      ]
    }
    """;

    [Fact]
    public void ParsesRepostWithQuote()
    {
        var posts = BlueskyComponentParser.Parse(JsonDocument.Parse(RepostMessage).RootElement, MsgTs, "f.json", "blesky");

        var post = Assert.Single(posts);
        Assert.Equal(Platform.Bluesky, post.Platform);
        Assert.Equal("ryanlcooper.com", post.Handle);
        Assert.Equal("ryan cooper", post.DisplayName);
        Assert.Equal("https://bsky.app/profile/did:plc:p5/post/3mpm2ogddt22w", post.Url); // fragment stripped
        Assert.Equal("it is a gross injustice #Politics", post.Text);
        Assert.Equal("ghaspey.bsky.social", post.RepostedBy);
        Assert.Equal("sharonk.bsky.social", post.QuotedHandle);
        Assert.Equal("so much of discourse #Discourse", post.QuotedText);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1782985063), post.Timestamp);
        Assert.Equal(new[] { "Politics", "Discourse" }, post.Hashtags);
    }

    [Fact]
    public void ReturnsNothingForMessagesWithoutComponents()
    {
        var posts = BlueskyComponentParser.Parse(
            JsonDocument.Parse("""{ "timestamp": "2026-07-02T09:37:45+00:00", "content": "hi" }""").RootElement,
            MsgTs, "f.json", "c");
        Assert.Empty(posts);
    }
}
