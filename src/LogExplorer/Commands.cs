namespace LogExplorer;

public static class Commands
{
    public static int Files(LogArchive archive, Dictionary<string, string> opts)
    {
        Console.WriteLine($"{"format",-24} {"platform",-9} {"range (UTC)",-33} {"msgs",6} {"posts",6}  file");
        foreach (var f in archive.Files)
        {
            var range = f.RangeFrom is null && f.RangeTo is null
                ? "-"
                : $"{f.RangeFrom:yyyy-MM-dd HH:mm} → {f.RangeTo:yyyy-MM-dd HH:mm}";
            var format = f.Format switch
            {
                ExportFormat.DiscordLogJson => "DiscordLog JSON",
                ExportFormat.DiscordChatExporterJson => "DiscordChatExporter",
                _ => $"{f.Format} (listed only)",
            };
            var name = f.Container is null ? f.Name : $"{f.Container} :: {f.Name}";
            var merged = f.MergedCount > 0 ? $"  [+{f.MergedCount} duplicate cop{(f.MergedCount == 1 ? "y" : "ies")} merged]" : "";
            Console.WriteLine($"{format,-24} {f.Platform,-9} {range,-33} {f.MessageCount,6} {f.ParsedPosts,6}  {name}{merged}"
                              + (f.Error is null ? "" : $"  [error: {f.Error}]"));
        }
        Console.WriteLine($"\n{archive.Files.Count} files ({archive.MergedFileCount} duplicate copies merged), " +
                          $"{archive.Posts.Count} unique posts after de-duplication");
        if (archive.Posts.Count > 0)
            Console.WriteLine($"overall coverage: {archive.Posts[0].Timestamp:yyyy-MM-dd HH:mm} → {archive.Posts[^1].Timestamp:yyyy-MM-dd HH:mm} UTC");
        return 0;
    }

    public static int Users(LogArchive archive, LuaFilterEngine lua, LabelEngine labels, Dictionary<string, string> opts)
    {
        var filter = Cli.BuildFilterOptions(opts, lua, archive);
        var posts = PostFilter.Apply(archive.Posts, filter, lua);
        var stats = archive.BuildUserStats(posts).ToList();

        if (opts.TryGetValue("find", out var find))
            stats = stats.Where(s => s.Handle.Contains(find, StringComparison.OrdinalIgnoreCase)
                                     || s.DisplayName.Contains(find, StringComparison.OrdinalIgnoreCase)).ToList();

        var sort = opts.GetValueOrDefault("sort", "posts");
        stats = sort switch
        {
            "posts" => stats.OrderByDescending(s => s.PostCount).ThenBy(s => s.Handle).ToList(),
            "recent" => stats.OrderByDescending(s => s.LastSeen).ToList(),
            "handle" => stats.OrderBy(s => s.Handle, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => throw new ArgumentException($"unknown --sort '{sort}'"),
        };

        var top = opts.TryGetValue("top", out var t) ? int.Parse(t) : int.MaxValue;
        var detail = opts.ContainsKey("detail");

        Console.WriteLine($"{"platform",-9} {"posts",6} {"reposts",8}  {"handle",-34} name");
        foreach (var s in stats.Take(top))
        {
            Console.WriteLine($"{s.Platform,-9} {s.PostCount,6} {s.RepostCount,8}  @{s.Handle,-33} {s.DisplayName}");
            if (detail)
            {
                var tags = string.Join(", ", s.HashtagCounts.OrderByDescending(kv => kv.Value).Take(8)
                    .Select(kv => $"#{kv.Key}×{kv.Value}"));
                var cats = string.Join(", ", s.CategoryCounts.OrderByDescending(kv => kv.Value)
                    .Select(kv => $"{kv.Key}×{kv.Value}"));
                Console.WriteLine($"{"",-26}seen {s.FirstSeen:yyyy-MM-dd} → {s.LastSeen:yyyy-MM-dd}"
                                  + (cats.Length > 0 ? $" | {cats}" : ""));
                if (tags.Length > 0) Console.WriteLine($"{"",-26}{tags}");
            }
        }
        Console.WriteLine($"\n{stats.Count} users ({stats.Count(s => s.Platform == Platform.Twitter)} twitter, " +
                          $"{stats.Count(s => s.Platform == Platform.Bluesky)} bluesky) across {posts.Count} matching posts");
        return 0;
    }

    public static int Posts(LogArchive archive, LuaFilterEngine lua, LabelEngine labels, Dictionary<string, string> opts)
    {
        var filter = Cli.BuildFilterOptions(opts, lua, archive);
        if (!opts.ContainsKey("limit") && !opts.ContainsKey("full")) filter.Limit = 50;
        var posts = PostFilter.Apply(archive.Posts, filter, lua);
        if (opts.GetValueOrDefault("sort") == "new") posts.Reverse();

        var full = opts.ContainsKey("full");
        foreach (var p in posts)
        {
            var text = full ? p.Text : Truncate(p.Text.Replace('\n', ' '), 120);
            var repost = p.RepostedBy is null ? "" : $" [rt by @{p.RepostedBy}]";
            var cats = p.Categories.Count > 0 ? $" [{string.Join(",", p.Categories)}]" : "";
            var subjects = p.Labels.Count > 0
                ? $" {{{string.Join(", ", p.Labels)}{(p.Kinds.Count > 0 ? " · " + string.Join("/", p.Kinds) : "")}}}"
                : "";
            Console.WriteLine($"{p.Timestamp:yyyy-MM-dd HH:mm} {PlatformTag(p.Platform)} @{p.Handle}{repost}{cats}{subjects}");
            Console.WriteLine($"    {text}");
            if (p.QuotedHandle is not null)
                Console.WriteLine($"    ↳ quoting @{p.QuotedHandle}: {(full ? p.QuotedText : Truncate(p.QuotedText?.Replace('\n', ' ') ?? "", 100))}");
            if (full) Console.WriteLine($"    {p.Url}");
        }
        Console.WriteLine($"\n{posts.Count} posts" + (posts.Count == filter.Limit ? $" (limited to {filter.Limit}; pass --limit to change)" : ""));
        return 0;
    }

    public static int Tags(LogArchive archive, LuaFilterEngine lua, LabelEngine labels, Dictionary<string, string> opts)
    {
        var filter = Cli.BuildFilterOptions(opts, lua, archive);
        var posts = PostFilter.Apply(archive.Posts, filter, lua);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in posts)
            foreach (var tag in p.Hashtags)
            {
                var key = HashtagExtractor.Normalize(tag);
                counts[key] = counts.GetValueOrDefault(key) + 1;
                display.TryAdd(key, tag);
            }

        var top = opts.TryGetValue("top", out var t) ? int.Parse(t) : 100;
        Console.WriteLine($"{"count",6}  {"hashtag",-32} categories");
        foreach (var (key, count) in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(top))
        {
            var cats = lua.Categories
                .Where(c => c.Tags.Any(ct => lua.TagMatches(key, ct)))
                .Select(c => c.Name);
            var catText = string.Join(", ", cats);
            Console.WriteLine($"{count,6}  #{display[key],-31} {(catText.Length > 0 ? catText : "-")}");
        }
        Console.WriteLine($"\n{counts.Count} distinct hashtags in {posts.Count} matching posts");
        return 0;
    }

    public static int Categories(LogArchive archive, LuaFilterEngine lua, LabelEngine labels, Dictionary<string, string> opts)
    {
        var filter = Cli.BuildFilterOptions(opts, lua, archive);
        var posts = PostFilter.Apply(archive.Posts, filter, lua);

        Console.WriteLine($"strict hashtag matching: {lua.Strict}\n");
        var byCat = new Dictionary<string, List<Post>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in posts)
            foreach (var c in p.Categories)
                (byCat.TryGetValue(c, out var list) ? list : byCat[c] = new List<Post>()).Add(p);

        foreach (var cat in lua.Categories)
        {
            var matched = byCat.GetValueOrDefault(cat.Name) ?? new List<Post>();
            Console.WriteLine($"## {cat.Name} — {matched.Count} posts, {matched.DistinctBy(p => (p.Platform, p.HandleKey)).Count()} users");
            if (cat.Tags.Count > 0)
                Console.WriteLine($"   tags: {string.Join(", ", cat.Tags.OrderBy(x => x).Select(x => "#" + x))}");
            if (cat.Users.Count > 0)
                Console.WriteLine($"   users: {string.Join(", ", cat.Users.OrderBy(x => x).Select(x => "@" + x))}");
            if (cat.MatchFn is not null)
                Console.WriteLine("   custom Lua match function: yes");
            var topUsers = matched.GroupBy(p => (p.Platform, p.HandleKey))
                .OrderByDescending(g => g.Count()).Take(5)
                .Select(g => $"@{g.First().Handle} ({g.Key.Item1}, {g.Count()})");
            if (matched.Count > 0)
                Console.WriteLine($"   top users: {string.Join(", ", topUsers)}");
            Console.WriteLine();
        }

        var uncat = byCat.GetValueOrDefault(lua.UncategorizedName) ?? new List<Post>();
        Console.WriteLine($"## {lua.UncategorizedName} — {uncat.Count} posts");
        return 0;
    }

    public static int Labels(LogArchive archive, LuaFilterEngine lua, LabelEngine labels, Dictionary<string, string> opts)
    {
        var filter = Cli.BuildFilterOptions(opts, lua, archive);
        var posts = PostFilter.Apply(archive.Posts, filter, lua);

        // Counts are recomputed over the filtered posts so labels respect --from/--kind/etc.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in posts)
            foreach (var name in p.Labels)
                counts[name] = counts.GetValueOrDefault(name) + 1;

        var rows = labels.Labels.Where(l => counts.ContainsKey(l.Display)).ToList();
        if (opts.TryGetValue("find", out var find))
            rows = rows.Where(l => l.Display.Contains(find, StringComparison.OrdinalIgnoreCase)
                                   || l.Variants.Any(v => v.Contains(find, StringComparison.OrdinalIgnoreCase))).ToList();
        if (opts.TryGetValue("kind", out var kindFilter))
        {
            var wanted = kindFilter.Split(',').Select(k => k.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            rows = rows.Where(l => wanted.Contains(l.Kind)).ToList();
        }

        var sort = opts.GetValueOrDefault("sort", "posts");
        rows = sort switch
        {
            "posts" => rows.OrderByDescending(l => counts[l.Display]).ThenBy(l => l.Display).ToList(),
            "users" => rows.OrderByDescending(l => l.UserCount).ThenBy(l => l.Display).ToList(),
            "name" => rows.OrderBy(l => l.Display, StringComparer.OrdinalIgnoreCase).ToList(),
            "kind" => rows.OrderBy(l => l.Kind, StringComparer.OrdinalIgnoreCase)
                          .ThenByDescending(l => counts[l.Display]).ToList(),
            _ => throw new ArgumentException($"unknown --sort '{sort}'"),
        };

        var top = opts.TryGetValue("top", out var t) ? int.Parse(t) : 60;
        var explain = opts.ContainsKey("explain");

        Console.WriteLine($"{"posts",6} {"users",6}  {"kind",-18} {"subject",-34} first seen → last seen");
        foreach (var l in rows.Take(top))
        {
            var mark = l.Inherited ? "~" : " ";
            Console.WriteLine($"{counts[l.Display],6} {l.UserCount,6}  {mark}{l.Kind,-17} {Truncate(l.Display, 34),-34} " +
                              $"{l.FirstSeen:yyyy-MM-dd} → {l.LastSeen:yyyy-MM-dd}");
            if (explain)
            {
                var how = l.Inherited
                    ? $"inherited from related subjects ({l.Confidence:P0} agreement)"
                    : l.Kind == labels.Options.UnknownName
                        ? "no vocabulary matched"
                        : $"vocabulary in {l.Confidence:P0} of its posts: {string.Join(", ", l.Evidence)}";
                Console.WriteLine($"{"",14}why: {how}");
                Console.WriteLine($"{"",14}spellings: {string.Join(" ", l.Variants.Take(6))}");
                if (l.Related.Count > 0)
                    Console.WriteLine($"{"",14}seen with: {string.Join(", ", l.Related)}");
            }
        }
        Console.WriteLine($"\n{rows.Count} subjects in {posts.Count} matching posts " +
                          $"({rows.Count(l => l.Kind == labels.Options.UnknownName)} unclassified, " +
                          $"~ = kind inherited from related subjects)");
        return 0;
    }

    public static int Kinds(LogArchive archive, LuaFilterEngine lua, LabelEngine labels, Dictionary<string, string> opts)
    {
        var filter = Cli.BuildFilterOptions(opts, lua, archive);
        var posts = PostFilter.Apply(archive.Posts, filter, lua);

        var postsPerKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in posts)
            foreach (var kind in p.Kinds)
                postsPerKind[kind] = postsPerKind.GetValueOrDefault(kind) + 1;

        Console.WriteLine("kinds are named in filters.lua and matched by the vocabulary listed there,");
        Console.WriteLine("never by a list of titles — new subjects are classified as they show up.\n");
        Console.WriteLine($"{"posts",7} {"subjects",9}  {"kind",-20} most common subjects");

        foreach (var kind in labels.Labels.Select(l => l.Kind).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(k => postsPerKind.GetValueOrDefault(k)))
        {
            var members = labels.Labels.Where(l => l.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => l.PostCount).ToList();
            var examples = string.Join(", ", members.Take(6).Select(l => l.Display));
            Console.WriteLine($"{postsPerKind.GetValueOrDefault(kind),7} {members.Count,9}  {kind,-20} {Truncate(examples, 70)}");
        }
        return 0;
    }

    public static int Export(LogArchive archive, LuaFilterEngine lua, LabelEngine labels, Dictionary<string, string> opts)
    {
        var filter = Cli.BuildFilterOptions(opts, lua, archive);
        var posts = PostFilter.Apply(archive.Posts, filter, lua);
        var format = opts.GetValueOrDefault("format", "json");
        var outPath = opts.GetValueOrDefault("out") ?? $"export.{format}";
        Exporters.Write(posts, outPath, format);
        Console.WriteLine($"wrote {posts.Count} posts to {outPath} ({format})");
        return 0;
    }

    private static string PlatformTag(Platform p) => p == Platform.Twitter ? "[tw]" : "[bs]";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
