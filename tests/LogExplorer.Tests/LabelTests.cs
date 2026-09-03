using LogExplorer;

namespace LogExplorer.Tests;

public class LabelTests : IDisposable
{
    private readonly string _scriptPath;

    // Note what is NOT in here: any title. The kinds are described purely by ordinary words,
    // which is the whole point — subject names are discovered from the posts.
    private const string Script = """
    options = { strict = true }
    categories = {}
    labels = {
        min_posts = 2,
        min_confidence = 0.05,
        min_terms = 2,
        learned_terms_per_kind = 0,
        propagate = true,
        propagate_share = 0.4,
        kinds = {
            ["Video game"] = { "gameplay", "patchnotes", "wishlist", "gamedev" },
            ["Anime"] = { "episode", "simulcast", "anime", "dubbed" },
        },
    }
    """;

    public LabelTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"labels-{Guid.NewGuid():N}.lua");
        File.WriteAllText(_scriptPath, Script);
    }

    public void Dispose() => File.Delete(_scriptPath);

    private static Post Post(string handle, string text, params string[] tags) => new()
    {
        Platform = Platform.Twitter,
        Handle = handle,
        DisplayName = handle,
        Text = text,
        Url = $"https://twitter.com/{handle}/status/{Guid.NewGuid():N}",
        Timestamp = DateTimeOffset.Parse("2026-07-10T08:00:00+00:00"),
        Hashtags = tags.ToList(),
    };

    private LabelEngine Build(params Post[] posts) =>
        LabelEngine.Build(posts, new LuaFilterEngine(_scriptPath));

    [Fact]
    public void NormalizesSpellingVariantsIntoOneSubject()
    {
        Assert.Equal("astralvoyage", LabelEngine.NormalizeKey("#Astral_Voyage"));
        Assert.Equal("astralvoyage", LabelEngine.NormalizeKey("AstralVoyage"));
        Assert.Equal("astralvoyage", LabelEngine.NormalizeKey("astral-voyage"));
    }

    [Fact]
    public void ReadsNamesOutOfCamelCaseButLeavesOtherScriptsAlone()
    {
        Assert.Equal("Astral Voyage", LabelEngine.SplitWords("AstralVoyage"));
        Assert.Equal("AX Cosplay", LabelEngine.SplitWords("AXCosplay"));
        Assert.Equal("astral voyage", LabelEngine.SplitWords("astral_voyage"));
        Assert.Equal("黄泉のツガイ", LabelEngine.SplitWords("黄泉のツガイ"));
    }

    [Fact]
    public void DiscoversSubjectsFromTheDataAndSkipsRareOnes()
    {
        var engine = Build(
            Post("a", "playing this", "AstralVoyage"),
            Post("b", "still playing", "AstralVoyage"),
            Post("c", "one mention only", "SomethingElse"));

        Assert.Contains(engine.Labels, l => l.Display == "Astral Voyage");
        Assert.DoesNotContain(engine.Labels, l => l.Display == "Something Else"); // below min_posts
    }

    [Fact]
    public void VocabularyHashtagsAreTopicsNotSubjects()
    {
        var engine = Build(
            Post("a", "new one", "anime", "AstralVoyage"),
            Post("b", "another", "anime", "AstralVoyage"));

        Assert.DoesNotContain(engine.Labels, l => l.Key == "anime");
        Assert.Contains(engine.Labels, l => l.Key == "astralvoyage");
    }

    [Fact]
    public void ClassifiesSubjectsByTheVocabularyAroundThem()
    {
        var engine = Build(
            Post("a", "the gameplay looks great, patchnotes are up", "AstralVoyage"),
            Post("b", "wishlist it now, the gameplay demo is live", "AstralVoyage"),
            Post("c", "episode 3 was great, dubbed cast is perfect", "SilverBloom"),
            Post("d", "simulcast of the next episode tomorrow", "SilverBloom"));

        Assert.Equal("Video game", engine.ByKey["astralvoyage"].Kind);
        Assert.Equal("Anime", engine.ByKey["silverbloom"].Kind);
        Assert.Contains("gameplay", engine.ByKey["astralvoyage"].Evidence);
    }

    [Fact]
    public void OneAmbiguousWordDoesNotDecideAKind()
    {
        // A single vocabulary hit, from someone who is not the subject's own account.
        var engine = Build(
            Post("a", "the gameplay", "AstralVoyage"),
            Post("b", "nice", "AstralVoyage"));

        Assert.Equal("Unclassified", engine.ByKey["astralvoyage"].Kind);
    }

    [Fact]
    public void TheSubjectsOwnAccountIsTrustedOnItsOwn()
    {
        var engine = Build(
            Post("astralvoyage", "our gameplay reveal is here", "AstralVoyage"),
            Post("b", "nice", "AstralVoyage"));

        Assert.Equal("Video game", engine.ByKey["astralvoyage"].Kind);
    }

    [Fact]
    public void UnclassifiedSubjectsInheritFromTheCompanyTheyKeep()
    {
        var engine = Build(
            Post("a", "the gameplay looks great, patchnotes are up", "AstralVoyage"),
            Post("b", "wishlist it, gameplay is smooth", "AstralVoyage"),
            Post("c", "no telling words here", "AstralVoyage", "Roscaelifer"),
            Post("d", "nor here", "AstralVoyage", "Roscaelifer"));

        var inherited = engine.ByKey["roscaelifer"];
        Assert.Equal("Video game", inherited.Kind);
        Assert.True(inherited.Inherited);
    }

    [Fact]
    public void PostsAreStampedWithTheirSubjectsAndKinds()
    {
        var posts = new[]
        {
            Post("a", "the gameplay looks great, patchnotes are up", "AstralVoyage"),
            Post("b", "wishlist it now, gameplay demo is live", "AstralVoyage"),
        };
        Build(posts);

        Assert.Equal(new[] { "Astral Voyage" }, posts[0].Labels);
        Assert.Equal(new[] { "Video game" }, posts[0].Kinds);
    }

    [Fact]
    public void FiltersBySubjectNameAndByKind()
    {
        var lua = new LuaFilterEngine(_scriptPath);
        var posts = new[]
        {
            Post("a", "the gameplay looks great, patchnotes are up", "AstralVoyage"),
            Post("b", "wishlist it now, gameplay demo is live", "AstralVoyage"),
            Post("c", "episode 3 was great, dubbed cast is perfect", "SilverBloom"),
            Post("d", "simulcast of the next episode tomorrow", "SilverBloom"),
        };
        LabelEngine.Build(posts, lua);

        // Both the readable name and the raw hashtag spelling address the same subject.
        Assert.Equal(2, PostFilter.Apply(posts, new FilterOptions { Labels = { "Astral Voyage" } }, lua).Count);
        Assert.Equal(2, PostFilter.Apply(posts, new FilterOptions { Labels = { "astralvoyage" } }, lua).Count);
        Assert.Equal(2, PostFilter.Apply(posts, new FilterOptions { Kinds = { "Anime" } }, lua).Count);
        Assert.Empty(PostFilter.Apply(posts, new FilterOptions { Kinds = { "Film & TV" } }, lua));
    }

    [Fact]
    public void LuaGetsTheFinalWordOnAKind()
    {
        var path = _scriptPath + ".detect.lua";
        File.WriteAllText(path, Script.Replace("propagate = true,",
            """
            propagate = true,
            detect = function(label) if label.posts >= 2 then return "Reviewed" end return nil end,
            """));
        try
        {
            var engine = LabelEngine.Build(
                new[]
                {
                    Post("a", "the gameplay looks great, patchnotes are up", "AstralVoyage"),
                    Post("b", "wishlist it now, gameplay demo is live", "AstralVoyage"),
                },
                new LuaFilterEngine(path));
            Assert.Equal("Reviewed", engine.ByKey["astralvoyage"].Kind);
        }
        finally { File.Delete(path); }
    }
}
