using MoonSharp.Interpreter;

namespace LogExplorer;

public sealed class CategoryDef
{
    public string Name = "";
    public HashSet<string> Tags = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Users = new(StringComparer.OrdinalIgnoreCase);
    public DynValue? MatchFn;
}

/// <summary>
/// Loads scripts/filters.lua and drives every filtering rule that lives on the Lua side:
/// hashtag categories (strict or loose matching), per-category user lists and custom match
/// functions, global exclusion rules, fallback categorization, and default date/time windows.
/// </summary>
public sealed class LuaFilterEngine
{
    private readonly Script _script;
    private readonly DynValue? _excludeFn;
    private readonly DynValue? _categorizeFn;

    public List<CategoryDef> Categories { get; } = new();
    public bool Strict { get; private set; } = true;
    public bool RequireCategory { get; private set; }
    public string UncategorizedName { get; private set; } = "Uncategorized";
    public DateTimeOffset? DefaultFrom { get; private set; }
    public DateTimeOffset? DefaultTo { get; private set; }
    public TimeSpan? DefaultTimeFrom { get; private set; }
    public TimeSpan? DefaultTimeTo { get; private set; }

    public LuaFilterEngine(string scriptPath)
    {
        _script = new Script(CoreModules.Preset_SoftSandbox);
        _script.Globals["log"] = (Action<string>)(msg => Console.Error.WriteLine($"[lua] {msg}"));
        _script.DoString(File.ReadAllText(scriptPath), codeFriendlyName: Path.GetFileName(scriptPath));

        var options = _script.Globals.Get("options");
        if (options.Type == DataType.Table)
        {
            var strict = options.Table.Get("strict");
            if (strict.Type == DataType.Boolean) Strict = strict.Boolean;
            var req = options.Table.Get("require_category");
            if (req.Type == DataType.Boolean) RequireCategory = req.Boolean;
            var unc = options.Table.Get("uncategorized_name");
            if (unc.Type == DataType.String) UncategorizedName = unc.String;
        }

        var date = _script.Globals.Get("date");
        if (date.Type == DataType.Table)
        {
            DefaultFrom = ParseDate(date.Table.Get("from"));
            DefaultTo = ParseDate(date.Table.Get("to"), endOfDay: true);
            DefaultTimeFrom = ParseTime(date.Table.Get("time_from"));
            DefaultTimeTo = ParseTime(date.Table.Get("time_to"));
        }

        var categories = _script.Globals.Get("categories");
        if (categories.Type == DataType.Table)
        {
            foreach (var pair in categories.Table.Pairs)
            {
                if (pair.Key.Type != DataType.String || pair.Value.Type != DataType.Table) continue;
                var def = new CategoryDef { Name = pair.Key.String };
                var table = pair.Value.Table;
                foreach (var tag in TableStrings(table.Get("tags")))
                    def.Tags.Add(HashtagExtractor.Normalize(tag));
                foreach (var user in TableStrings(table.Get("users")))
                    def.Users.Add(user.TrimStart('@'));
                var fn = table.Get("match");
                if (fn.Type == DataType.Function) def.MatchFn = fn;
                Categories.Add(def);
            }
            Categories.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        var exclude = _script.Globals.Get("exclude");
        if (exclude.Type == DataType.Function) _excludeFn = exclude;
        var categorize = _script.Globals.Get("categorize");
        if (categorize.Type == DataType.Function) _categorizeFn = categorize;
    }

    /// <summary>Strict = exact hashtag match; loose = the post tag may merely start with a category tag.</summary>
    public bool TagMatches(string postTag, string categoryTag) =>
        Strict
            ? postTag.Equals(categoryTag, StringComparison.OrdinalIgnoreCase)
            : postTag.StartsWith(categoryTag, StringComparison.OrdinalIgnoreCase);

    public bool IsExcluded(Post post) =>
        _excludeFn is not null && _script.Call(_excludeFn, ToLuaTable(post)).CastToBool();

    public void AssignCategories(Post post)
    {
        var normalized = post.Hashtags.Select(HashtagExtractor.Normalize).ToList();
        var result = new List<string>();
        DynValue? luaPost = null;

        foreach (var cat in Categories)
        {
            var applies = normalized.Any(t => cat.Tags.Any(ct => TagMatches(t, ct)))
                          || cat.Users.Contains(post.Handle);
            if (!applies && cat.MatchFn is not null)
            {
                luaPost ??= ToLuaTable(post);
                applies = _script.Call(cat.MatchFn, luaPost).CastToBool();
            }
            if (applies) result.Add(cat.Name);
        }

        if (result.Count == 0 && _categorizeFn is not null)
        {
            luaPost ??= ToLuaTable(post);
            var extra = _script.Call(_categorizeFn, luaPost);
            if (extra.Type == DataType.String) result.Add(extra.String);
            else if (extra.Type == DataType.Table) result.AddRange(TableStrings(extra));
        }

        if (result.Count == 0) result.Add(UncategorizedName);
        post.Categories = result;
    }

    /// <summary>Compiles an ad-hoc `--where` Lua expression into a per-post predicate.</summary>
    public Func<Post, bool> CompileWhere(string expression)
    {
        var fn = _script.DoString($"return function(post) return ({expression}) end", codeFriendlyName: "--where");
        return post => _script.Call(fn, ToLuaTable(post)).CastToBool();
    }

    public DynValue ToLuaTable(Post post)
    {
        var t = new Table(_script)
        {
            ["platform"] = post.Platform.ToString().ToLowerInvariant(),
            ["handle"] = post.Handle,
            ["display_name"] = post.DisplayName,
            ["text"] = post.Text,
            ["url"] = post.Url,
            ["timestamp"] = post.Timestamp.ToString("o"),
            ["unix"] = (double)post.Timestamp.ToUnixTimeSeconds(),
            ["date"] = post.Timestamp.ToString("yyyy-MM-dd"),
            ["time"] = post.Timestamp.ToString("HH:mm:ss"),
            ["reposted_by"] = post.RepostedBy,
            ["is_repost"] = post.RepostedBy is not null,
            ["quoted_handle"] = post.QuotedHandle,
            ["quoted_text"] = post.QuotedText,
            ["channel"] = post.Channel,
            ["source_file"] = post.SourceFile,
        };
        var tags = new Table(_script);
        for (int i = 0; i < post.Hashtags.Count; i++)
            tags[i + 1] = HashtagExtractor.Normalize(post.Hashtags[i]);
        t["hashtags"] = tags;
        var cats = new Table(_script);
        for (int i = 0; i < post.Categories.Count; i++)
            cats[i + 1] = post.Categories[i];
        t["categories"] = cats;
        return DynValue.NewTable(t);
    }

    private static IEnumerable<string> TableStrings(DynValue value)
    {
        if (value.Type != DataType.Table) yield break;
        foreach (var v in value.Table.Values)
            if (v.Type == DataType.String)
                yield return v.String;
    }

    private static DateTimeOffset? ParseDate(DynValue value, bool endOfDay = false)
    {
        if (value.Type != DataType.String) return null;
        return Cli.ParseDateArg(value.String, endOfDay);
    }

    private static TimeSpan? ParseTime(DynValue value) =>
        value.Type == DataType.String && TimeSpan.TryParse(value.String, out var ts) ? ts : null;
}
