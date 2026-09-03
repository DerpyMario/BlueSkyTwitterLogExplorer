using System.Text;
using System.Text.Json;

namespace LogExplorer;

public sealed class FilterOptions
{
    public Platform? Platform;
    public List<string> Users = new();
    public List<string> Tags = new();
    public List<string> Categories = new();

    /// <summary>Subject names to keep, matched against the auto-detected labels
    /// ("Genshin Impact" and "genshinimpact" both work).</summary>
    public List<string> Labels = new();

    /// <summary>Kinds of subject to keep ("Video game", "Anime", …).</summary>
    public List<string> Kinds = new();
    public DateTimeOffset? From;
    public DateTimeOffset? To;
    public TimeSpan? TimeFrom;
    public TimeSpan? TimeTo;
    public string? Contains;
    public string? WhereLua;
    public bool ExcludeReposts;
    public bool OnlyReposts;
    public int Limit = int.MaxValue;

    /// <summary>Date/time windows not set on the command line fall back to the Lua defaults automatically.</summary>
    public void ApplyLuaDefaults(LuaFilterEngine lua)
    {
        From ??= lua.DefaultFrom;
        To ??= lua.DefaultTo;
        TimeFrom ??= lua.DefaultTimeFrom;
        TimeTo ??= lua.DefaultTimeTo;
    }
}

public static class PostFilter
{
    public static List<Post> Apply(IEnumerable<Post> posts, FilterOptions o, LuaFilterEngine lua)
    {
        var users = o.Users.Select(u => u.TrimStart('@').ToLowerInvariant()).ToHashSet();
        var tags = o.Tags.Select(HashtagExtractor.Normalize).ToList();
        var categories = o.Categories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var labels = o.Labels.Select(LabelEngine.NormalizeKey).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kinds = o.Kinds.Select(k => k.Trim()).Where(k => k.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var where = o.WhereLua is null ? null : lua.CompileWhere(o.WhereLua);

        var result = new List<Post>();
        foreach (var post in posts)
        {
            if (o.Platform is { } p && post.Platform != p) continue;
            if (users.Count > 0 && !users.Contains(post.HandleKey)
                && (post.RepostedBy is null || !users.Contains(post.RepostedBy.ToLowerInvariant()))) continue;
            if (o.From is { } from && post.Timestamp < from) continue;
            if (o.To is { } to && post.Timestamp > to) continue;
            if (!InTimeWindow(post.Timestamp, o.TimeFrom, o.TimeTo)) continue;
            if (o.ExcludeReposts && post.RepostedBy is not null) continue;
            if (o.OnlyReposts && post.RepostedBy is null) continue;
            if (tags.Count > 0 && !post.Hashtags
                    .Select(HashtagExtractor.Normalize)
                    .Any(pt => tags.Any(t => lua.TagMatches(pt, t)))) continue;
            if (categories.Count > 0 && !post.Categories.Any(categories.Contains)) continue;
            if (labels.Count > 0 && !post.Labels.Any(l => labels.Contains(LabelEngine.NormalizeKey(l)))) continue;
            if (kinds.Count > 0 && !post.Kinds.Any(kinds.Contains)) continue;
            if (lua.RequireCategory && post.Categories.Count == 1 && post.Categories[0] == lua.UncategorizedName
                && !categories.Contains(lua.UncategorizedName)) continue;
            if (o.Contains is { } c
                && !(post.Text.Contains(c, StringComparison.OrdinalIgnoreCase)
                     || (post.QuotedText?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false))) continue;
            if (lua.IsExcluded(post)) continue;
            if (where is not null && !where(post)) continue;

            result.Add(post);
            if (result.Count >= o.Limit) break;
        }
        return result;
    }

    private static bool InTimeWindow(DateTimeOffset ts, TimeSpan? from, TimeSpan? to)
    {
        if (from is null && to is null) return true;
        var t = ts.UtcDateTime.TimeOfDay;
        var lo = from ?? TimeSpan.Zero;
        var hi = to ?? TimeSpan.FromDays(1);
        // Window may wrap midnight (e.g. 22:00-06:00).
        return lo <= hi ? t >= lo && t <= hi : t >= lo || t <= hi;
    }
}

public static class Exporters
{
    public static void Write(List<Post> posts, string path, string format)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        switch (format.ToLowerInvariant())
        {
            case "json":
                writer.Write(JsonSerializer.Serialize(posts, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
                break;
            case "csv":
                writer.WriteLine("timestamp,platform,handle,display_name,reposted_by,categories,labels,kinds,hashtags,url,text");
                foreach (var p in posts)
                    writer.WriteLine(string.Join(",",
                        Csv(p.Timestamp.ToString("o")), Csv(p.Platform.ToString()), Csv(p.Handle),
                        Csv(p.DisplayName), Csv(p.RepostedBy ?? ""), Csv(string.Join(";", p.Categories)),
                        Csv(string.Join(";", p.Labels)), Csv(string.Join(";", p.Kinds)),
                        Csv(string.Join(";", p.Hashtags)), Csv(p.Url), Csv(p.Text)));
                break;
            case "md":
                writer.WriteLine("# Filtered posts\n");
                foreach (var p in posts)
                {
                    writer.WriteLine($"### {p.DisplayName} (@{p.Handle}) — {p.Platform} — {p.Timestamp:yyyy-MM-dd HH:mm} UTC");
                    if (p.RepostedBy is not null) writer.WriteLine($"*Reposted by @{p.RepostedBy}*");
                    writer.WriteLine();
                    writer.WriteLine(p.Text);
                    if (p.QuotedHandle is not null)
                        writer.WriteLine($"\n> Quoting @{p.QuotedHandle}: {p.QuotedText}");
                    writer.WriteLine($"\nCategories: {string.Join(", ", p.Categories)}  ");
                    if (p.Labels.Count > 0)
                        writer.WriteLine($"Labels: {string.Join(", ", p.Labels)}"
                                         + (p.Kinds.Count > 0 ? $" ({string.Join(", ", p.Kinds)})" : "") + "  ");
                    writer.WriteLine($"Link: <{p.Url}>\n");
                }
                break;
            default:
                throw new ArgumentException($"unknown export format '{format}' (expected json, csv or md)");
        }
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
}
