using LogExplorer;

namespace LogExplorer.Tests;

public class TuningTests : IDisposable
{
    private readonly string _dir;
    private readonly string _scriptPath;

    private const string Script = """
    options = { strict = true }
    categories = {}
    labels = {
        min_posts = 3,
        min_terms = 2,
        learned_terms_per_kind = 0,
        kinds = {
            ["Video game"] = { "gameplay", "patchnotes", "wishlist" },
            ["Anime"] = { "episode", "simulcast", "dubbed" },
        },
    }
    """;

    public TuningTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"logexplorer-tuning-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _scriptPath = Path.Combine(_dir, "filters.lua");
        File.WriteAllText(_scriptPath, Script);
    }

    public void Dispose() => Directory.Delete(_dir, true);

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

    [Fact]
    public void CapturesWhatTheScriptSaysSoItCanBeEdited()
    {
        var lua = new LuaFilterEngine(_scriptPath);
        var tuning = LabelTuning.CaptureFrom(lua.LabelOptions);

        Assert.Equal(3, tuning.MinPosts);
        Assert.Equal(2, tuning.MinTerms);
        Assert.Equal(2, tuning.Kinds.Count);
        Assert.Contains(tuning.Kinds, k => k.Name == "Video game" && k.Terms.Contains("gameplay"));
    }

    [Fact]
    public void SurvivesASaveAndLoadRoundTrip()
    {
        var lua = new LuaFilterEngine(_scriptPath);
        var tuning = LabelTuning.CaptureFrom(lua.LabelOptions);
        tuning.MinPosts = 7;
        tuning.MinMargin = 2.5;
        tuning.Kinds.Add(new KindTuning { Name = "Podcast", Weight = 0.8, Terms = new List<string> { "episode drop" } });

        var path = LabelTuning.PathFor(_scriptPath);
        tuning.Save(path);
        var reloaded = LabelTuning.Load(path);

        Assert.NotNull(reloaded);
        Assert.Equal(7, reloaded!.MinPosts);
        Assert.Equal(2.5, reloaded.MinMargin);
        Assert.Contains(reloaded.Kinds, k => k.Name == "Podcast" && k.Weight == 0.8);
        Assert.Equal(Path.Combine(_dir, LabelTuning.FileName), path);
    }

    [Fact]
    public void SavedSettingsOverrideTheScript()
    {
        var tuning = LabelTuning.CaptureFrom(new LuaFilterEngine(_scriptPath).LabelOptions);
        tuning.MinPosts = 9;
        tuning.Kinds = new List<KindTuning>
        {
            new() { Name = "Tabletop", Weight = 1.0, Terms = new List<string> { "campaign", "dice" } },
        };
        tuning.Save(LabelTuning.PathFor(_scriptPath));

        var lua = new LuaFilterEngine(_scriptPath);
        var used = LabelTuning.ApplySaved(_scriptPath, lua.LabelOptions);

        Assert.NotNull(used);
        Assert.Equal(9, lua.LabelOptions.MinPosts);
        var kind = Assert.Single(lua.LabelOptions.Kinds);
        Assert.Equal("Tabletop", kind.Name);
    }

    [Fact]
    public void WithNoSavedSettingsTheScriptIsLeftAlone()
    {
        var lua = new LuaFilterEngine(_scriptPath);
        Assert.Null(LabelTuning.ApplySaved(_scriptPath, lua.LabelOptions));
        Assert.Equal(3, lua.LabelOptions.MinPosts);
    }

    [Fact]
    public void AnEmptyKindListIsIgnoredRatherThanWipingTheVocabulary()
    {
        var lua = new LuaFilterEngine(_scriptPath);
        new LabelTuning { Kinds = new List<KindTuning>() }.ApplyTo(lua.LabelOptions);
        Assert.Equal(2, lua.LabelOptions.Kinds.Count);
    }

    [Fact]
    public void ChangedSettingsChangeWhatIsDetected()
    {
        var posts = new[]
        {
            Post("a", "the gameplay looks great, patchnotes are up", "AstralVoyage"),
            Post("b", "wishlist it now, the gameplay demo is live", "AstralVoyage"),
            Post("c", "more gameplay footage", "AstralVoyage"),
        };

        var lua = new LuaFilterEngine(_scriptPath);
        Assert.Equal("Video game", LabelEngine.Build(posts, lua).ByKey["astralvoyage"].Kind);

        // Raising the bar past what this subject can show leaves it undecided...
        var strict = LabelTuning.CaptureFrom(lua.LabelOptions);
        strict.MinPosts = 10;
        strict.ApplyTo(lua.LabelOptions);
        Assert.Empty(LabelEngine.Build(posts, lua).Labels);

        // ...and lowering it again brings it back, which is what the GUI's Apply does.
        strict.MinPosts = 2;
        strict.ApplyTo(lua.LabelOptions);
        Assert.Equal("Video game", LabelEngine.Build(posts, lua).ByKey["astralvoyage"].Kind);
    }

    [Fact]
    public void KindsAddedInTheGuiAreUsedForDetection()
    {
        var posts = new[]
        {
            Post("a", "our campaign wrapped, the dice were kind", "Roscaelifer"),
            Post("b", "session two of the campaign, dice hated me", "Roscaelifer"),
            Post("c", "campaign night, bring dice", "Roscaelifer"),
        };

        var lua = new LuaFilterEngine(_scriptPath);
        Assert.Equal("Unclassified", LabelEngine.Build(posts, lua).ByKey["roscaelifer"].Kind);

        var tuning = LabelTuning.CaptureFrom(lua.LabelOptions);
        tuning.Kinds.Add(new KindTuning { Name = "Tabletop", Weight = 1.0, Terms = new List<string> { "campaign", "dice" } });
        tuning.ApplyTo(lua.LabelOptions);

        Assert.Equal("Tabletop", LabelEngine.Build(posts, lua).ByKey["roscaelifer"].Kind);
    }
}
