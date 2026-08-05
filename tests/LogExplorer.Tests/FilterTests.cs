using LogExplorer;

namespace LogExplorer.Tests;

public class LuaFilterEngineTests : IDisposable
{
    private readonly string _scriptPath;

    private const string Script = """
    options = { strict = true }
    date = { from = "2026-07-01", to = "2026-07-31", time_from = "06:00", time_to = "12:00" }
    categories = {
        Anime = { tags = { "anime", "manga" } },
        Friends = { users = { "friend.bsky.social" } },
        Long = { match = function(post) return string.len(post.text) > 100 end },
    }
    function exclude(post)
        return post.handle == "spammer"
    end
    """;

    public LuaFilterEngineTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"filters-{Guid.NewGuid():N}.lua");
        File.WriteAllText(_scriptPath, Script);
    }

    public void Dispose() => File.Delete(_scriptPath);

    private static Post MakePost(string handle = "someone", string text = "", params string[] tags) => new()
    {
        Platform = Platform.Twitter,
        Handle = handle,
        Text = text,
        Url = $"https://twitter.com/{handle}/status/1",
        Timestamp = DateTimeOffset.Parse("2026-07-10T08:00:00+00:00"),
        Hashtags = tags.ToList(),
    };

    [Fact]
    public void StrictMatchingRequiresExactTag()
    {
        var lua = new LuaFilterEngine(_scriptPath);
        Assert.True(lua.TagMatches("anime", "anime"));
        Assert.True(lua.TagMatches("ANIME", "anime"));
        Assert.False(lua.TagMatches("animeexpo", "anime"));
    }

    [Fact]
    public void LooseMatchingAllowsPrefix()
    {
        var loosePath = _scriptPath + ".loose.lua";
        File.WriteAllText(loosePath, "options = { strict = false }\ncategories = {}");
        try
        {
            var lua = new LuaFilterEngine(loosePath);
            Assert.True(lua.TagMatches("animeexpo", "anime"));
            Assert.False(lua.TagMatches("anime", "animeexpo"));
        }
        finally { File.Delete(loosePath); }
    }

    [Fact]
    public void AssignsCategoriesByTagUserAndLuaFunction()
    {
        var lua = new LuaFilterEngine(_scriptPath);

        var byTag = MakePost(tags: "Anime");
        lua.AssignCategories(byTag);
        Assert.Equal(new[] { "Anime" }, byTag.Categories);

        var byUser = MakePost(handle: "friend.bsky.social");
        lua.AssignCategories(byUser);
        Assert.Equal(new[] { "Friends" }, byUser.Categories);

        var byFn = MakePost(text: new string('x', 150));
        lua.AssignCategories(byFn);
        Assert.Equal(new[] { "Long" }, byFn.Categories);

        var nothing = MakePost();
        lua.AssignCategories(nothing);
        Assert.Equal(new[] { "Uncategorized" }, nothing.Categories);
    }

    [Fact]
    public void ExcludeFunctionDropsPosts()
    {
        var lua = new LuaFilterEngine(_scriptPath);
        Assert.True(lua.IsExcluded(MakePost(handle: "spammer")));
        Assert.False(lua.IsExcluded(MakePost(handle: "fine")));
    }

    [Fact]
    public void LuaDateAndTimeDefaultsApplyAutomatically()
    {
        var lua = new LuaFilterEngine(_scriptPath);
        var options = new FilterOptions();
        options.ApplyLuaDefaults(lua);

        Assert.Equal(DateTimeOffset.Parse("2026-07-01T00:00:00+00:00"), options.From);
        Assert.Equal(31, options.To!.Value.Day); // inclusive whole day
        Assert.Equal(TimeSpan.FromHours(6), options.TimeFrom);
        Assert.Equal(TimeSpan.FromHours(12), options.TimeTo);

        var posts = new[]
        {
            MakePost(handle: "in-window"),                                       // 07-10 08:00 ✓
            MakePost(handle: "too-early").At("2026-06-20T08:00:00+00:00"),    // before July ✗
            MakePost(handle: "wrong-time").At("2026-07-10T20:00:00+00:00"),   // outside 06-12 ✗
        };
        var result = PostFilter.Apply(posts, options, lua);
        Assert.Equal(new[] { "in-window" }, result.Select(p => p.Handle));
    }

    [Fact]
    public void WhereExpressionFiltersPosts()
    {
        var lua = new LuaFilterEngine(_scriptPath);
        var options = new FilterOptions { WhereLua = "#post.hashtags > 1" };
        var posts = new[] { MakePost("someone", "", "a", "b"), MakePost("single", "", "a") };
        var result = PostFilter.Apply(posts, options, lua);
        Assert.Equal(new[] { "someone" }, result.Select(p => p.Handle));
    }

    [Fact]
    public void TimeWindowMayWrapMidnight()
    {
        var lua = new LuaFilterEngine(_scriptPath);
        var options = new FilterOptions { TimeFrom = TimeSpan.FromHours(22), TimeTo = TimeSpan.FromHours(6) };
        var posts = new[]
        {
            MakePost(handle: "night").At("2026-07-10T23:30:00+00:00"),
            MakePost(handle: "morning").At("2026-07-10T05:00:00+00:00"),
            MakePost(handle: "noon").At("2026-07-10T12:00:00+00:00"),
        };
        var result = PostFilter.Apply(posts, options, lua);
        Assert.Equal(new[] { "morning", "night" }, result.Select(p => p.Handle).OrderBy(x => x));
    }
}

internal static class PostTestExtensions
{
    /// <summary>Returns the post with its timestamp replaced (Post is mutable; helper for fixtures).</summary>
    public static Post At(this Post post, string timestamp)
    {
        post.Timestamp = DateTimeOffset.Parse(timestamp);
        return post;
    }
}
