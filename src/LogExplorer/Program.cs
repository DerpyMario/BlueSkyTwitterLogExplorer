using System.Globalization;

namespace LogExplorer;

public static class Cli
{
    private const string DefaultFilters = "scripts/filters.lua";

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0];
        var opts = ParseArgs(args.Skip(1));

        var dataDir = opts.GetValueOrDefault("data") ?? Environment.GetEnvironmentVariable("LOGEXPLORER_DATA") ?? "data";
        if (!Directory.Exists(dataDir))
            throw new DirectoryNotFoundException(
                $"data directory '{dataDir}' not found — pass --data <dir> pointing at the extracted DiscordLog exports");

        var filtersPath = opts.GetValueOrDefault("filters")
            ?? (File.Exists(DefaultFilters) ? DefaultFilters
                : Path.Combine(AppContext.BaseDirectory, "scripts", "filters.lua"));
        if (!File.Exists(filtersPath))
            throw new FileNotFoundException($"Lua filter script not found at '{filtersPath}' — pass --filters <file>");

        Console.Error.WriteLine($"loading exports from {dataDir} ...");
        var archive = LogArchive.Load(dataDir);
        var lua = new LuaFilterEngine(filtersPath);
        foreach (var post in archive.Posts)
            lua.AssignCategories(post);
        Console.Error.WriteLine(
            $"loaded {archive.Files.Count} files, {archive.Posts.Count} unique posts " +
            $"({archive.Posts.Count(p => p.Platform == Platform.Twitter)} twitter, " +
            $"{archive.Posts.Count(p => p.Platform == Platform.Bluesky)} bluesky) — " +
            $"filters: {Path.GetFileName(filtersPath)} (strict={lua.Strict})");

        return Dispatch(command, opts, archive, lua);
    }

    private static int Dispatch(string command, Dictionary<string, string> opts, LogArchive archive, LuaFilterEngine lua)
    {
        switch (command)
        {
            case "files": return Commands.Files(archive, opts);
            case "users": return Commands.Users(archive, lua, opts);
            case "posts": return Commands.Posts(archive, lua, opts);
            case "tags": return Commands.Tags(archive, lua, opts);
            case "categories": return Commands.Categories(archive, lua, opts);
            case "export": return Commands.Export(archive, lua, opts);
            case "interactive": return Interactive(archive, lua);
            default:
                Console.Error.WriteLine($"unknown command '{command}'");
                PrintHelp();
                return 2;
        }
    }

    private static int Interactive(LogArchive archive, LuaFilterEngine lua)
    {
        Console.WriteLine("interactive explorer — type a command (e.g. 'users --top 20'), 'help' or 'quit'");
        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null || line.Trim() is "quit" or "exit") return 0;
            if (line.Trim().Length == 0) continue;
            if (line.Trim() == "help") { PrintHelp(); continue; }
            var parts = SplitCommandLine(line);
            try
            {
                Dispatch(parts[0], ParseArgs(parts.Skip(1)), archive, lua);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
            }
        }
    }

    public static FilterOptions BuildFilterOptions(Dictionary<string, string> opts, LuaFilterEngine lua, LogArchive archive)
    {
        var o = new FilterOptions();
        if (opts.TryGetValue("platform", out var p))
            o.Platform = p.ToLowerInvariant() switch
            {
                "twitter" or "x" => Platform.Twitter,
                "bluesky" or "bsky" => Platform.Bluesky,
                _ => throw new ArgumentException($"unknown platform '{p}'"),
            };
        if (opts.TryGetValue("user", out var u)) o.Users = u.Split(',').Select(s => s.Trim()).ToList();
        if (opts.TryGetValue("tag", out var t)) o.Tags = t.Split(',').Select(s => s.Trim()).ToList();
        if (opts.TryGetValue("category", out var c)) o.Categories = c.Split(',').Select(s => s.Trim()).ToList();
        if (opts.TryGetValue("from", out var f)) o.From = ParseDateArg(f, endOfDay: false);
        if (opts.TryGetValue("to", out var to)) o.To = ParseDateArg(to, endOfDay: true);
        if (opts.TryGetValue("last", out var last)) ApplyLast(o, last, archive);
        if (opts.TryGetValue("between", out var b)) ApplyBetween(o, b);
        if (opts.TryGetValue("contains", out var ct)) o.Contains = ct;
        if (opts.TryGetValue("where", out var w)) o.WhereLua = w;
        if (opts.ContainsKey("no-reposts")) o.ExcludeReposts = true;
        if (opts.ContainsKey("only-reposts")) o.OnlyReposts = true;
        if (opts.TryGetValue("limit", out var l)) o.Limit = int.Parse(l);
        o.ApplyLuaDefaults(lua);
        return o;
    }

    /// <summary>--last 7d / 12h / 45m, measured back from the newest post in the dataset.</summary>
    private static void ApplyLast(FilterOptions o, string spec, LogArchive archive)
    {
        if (archive.Posts.Count == 0) return;
        var newest = archive.Posts[^1].Timestamp;
        var n = double.Parse(spec[..^1], CultureInfo.InvariantCulture);
        o.From = spec[^1] switch
        {
            'd' => newest.AddDays(-n),
            'h' => newest.AddHours(-n),
            'm' => newest.AddMinutes(-n),
            _ => throw new ArgumentException($"bad --last '{spec}' (use e.g. 7d, 12h, 30m)"),
        };
        o.To ??= newest;
    }

    /// <summary>--between 06:00-12:00 — time-of-day window (UTC), may wrap midnight.</summary>
    private static void ApplyBetween(FilterOptions o, string spec)
    {
        var parts = spec.Split('-', 2);
        if (parts.Length != 2 || !TimeSpan.TryParse(parts[0], out var lo) || !TimeSpan.TryParse(parts[1], out var hi))
            throw new ArgumentException($"bad --between '{spec}' (use e.g. 06:00-12:00)");
        o.TimeFrom = lo;
        o.TimeTo = hi;
    }

    public static DateTimeOffset ParseDateArg(string value, bool endOfDay)
    {
        var formats = new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" };
        if (DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
        {
            // A bare date used as an upper bound means "through the end of that day".
            if (endOfDay && value.Length == 10) parsed = parsed.AddDays(1).AddTicks(-1);
            return parsed;
        }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
            return parsed;
        throw new ArgumentException($"cannot parse date/time '{value}'");
    }

    public static Dictionary<string, string> ParseArgs(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>();
        string? key = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--"))
            {
                if (key is not null) result[key] = "";
                var eq = arg.IndexOf('=');
                if (eq >= 0)
                {
                    result[arg[2..eq]] = arg[(eq + 1)..];
                    key = null;
                }
                else key = arg[2..];
            }
            else if (key is not null)
            {
                result[key] = arg;
                key = null;
            }
            else throw new ArgumentException($"unexpected argument '{arg}'");
        }
        if (key is not null) result[key] = "";
        return result;
    }

    private static List<string> SplitCommandLine(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ' ' && !quoted)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        BlueSky/Twitter DiscordLog explorer — C# + Lua

        usage: logexplorer <command> [options]

        commands:
          files          list every export file with format, channel, date range and parsed post counts
          users          list every Bluesky/Twitter user seen in the logs
          posts          list posts matching the filters
          tags           list hashtags with counts and the category they map to
          categories     show Lua-defined categories with post/user counts
          export         write filtered posts to a file (--out, --format json|csv|md)
          interactive    REPL that accepts the commands above

        common options:
          --data <dir>        export directory (default ./data, or $LOGEXPLORER_DATA)
          --filters <file>    Lua filter script (default scripts/filters.lua)
          --platform <p>      twitter | bluesky
          --user <a,b>        only these handles (author or reposter, exact match)
          --tag <a,b>         only posts with these hashtags (strictness from Lua options.strict)
          --category <a,b>    only posts in these Lua categories
          --from/--to <date>  date or date-time window, e.g. 2026-07-01 or 2026-07-01T12:30 (UTC)
          --last <n[dhm]>     window measured back from the newest post, e.g. --last 7d
          --between <t1-t2>   time-of-day window (UTC), e.g. 06:00-12:00 (may wrap midnight)
          --contains <text>   full-text search
          --where <lua>       ad-hoc Lua predicate, e.g. "post.is_repost and #post.hashtags > 1"
          --no-reposts / --only-reposts
          --limit <n>

        command-specific:
          users:  --sort posts|recent|handle   --top <n>   --find <substr>   --detail
          posts:  --full (untruncated text)    --sort old|new
          export: --out <path>   --format json|csv|md
        """);
    }
}
