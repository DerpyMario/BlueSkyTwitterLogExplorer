using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogExplorer;

public static partial class HashtagExtractor
{
    [GeneratedRegex(@"\[([^\]]*)\]\(<?[^)]+>?\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"<a?:\w+:\d+>|<t:\d+(:[a-zA-Z])?>|<@!?\d+>|<#\d+>")]
    private static partial Regex DiscordMarkup();

    [GeneratedRegex(@"#([\p{L}\p{N}_]+)")]
    private static partial Regex Hashtag();

    /// <summary>
    /// Extracts hashtags from post text. Markdown links are reduced to their label and raw URLs are
    /// removed first, so URL fragments such as "#repostId=..." never count as hashtags.
    /// Unicode letters are supported (Japanese hashtags are common in this dataset).
    /// </summary>
    public static List<string> Extract(params string?[] texts)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            var cleaned = MarkdownLink().Replace(text, "$1");
            cleaned = BareUrl().Replace(cleaned, " ");
            cleaned = DiscordMarkup().Replace(cleaned, " ");
            foreach (Match m in Hashtag().Matches(cleaned))
            {
                // Pure numbers ("#1 fan") are not hashtags on either platform.
                var tag = m.Groups[1].Value;
                if (tag.All(char.IsDigit)) continue;
                if (seen.Add(tag)) result.Add(tag);
            }
        }
        return result;
    }

    public static string Normalize(string tag) => tag.TrimStart('#').ToLowerInvariant();
}

public static partial class TweetEmbedParser
{
    [GeneratedRegex(@"^https?://(?:www\.)?(?:twitter\.com|x\.com)/([A-Za-z0-9_]+)/status(?:es)?/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StatusUrl();

    [GeneratedRegex(@"^(?<name>.*?)\s*\(@(?<handle>[A-Za-z0-9_]+)\)\s*\W*$")]
    private static partial Regex AuthorName();

    public static bool IsStatusUrl(string? url) => url is not null && StatusUrl().IsMatch(url);

    /// <summary>
    /// Builds a Post from one tweet embed. Works for both raw Discord API embeds (DiscordLog)
    /// and DiscordChatExporter embeds — the fields used (url, description, timestamp, author.name)
    /// are named identically in both.
    /// </summary>
    public static Post? Parse(JsonElement embed, DateTimeOffset messageTimestamp, string sourceFile, string channel)
    {
        var url = embed.GetStringProp("url");
        var m = url is null ? null : StatusUrl().Match(url) is { Success: true } sm ? sm : null;
        if (m is null) return null;

        var urlHandle = m.Groups[1].Value;
        var canonicalUrl = $"https://twitter.com/{urlHandle}/status/{m.Groups[2].Value}";

        string? authorRaw = null;
        if (embed.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
            authorRaw = author.GetStringProp("name");

        var handle = urlHandle;
        var displayName = authorRaw ?? urlHandle;
        string? repostedBy = null;
        if (authorRaw is not null && AuthorName().Match(authorRaw) is { Success: true } am)
        {
            handle = am.Groups["handle"].Value;
            displayName = am.Groups["name"].Value.Trim();
            // TweetShift retweets keep the retweeter's status URL but show the original author
            // in the embed header, so a handle mismatch means "urlHandle retweeted handle".
            if (!handle.Equals(urlHandle, StringComparison.OrdinalIgnoreCase))
                repostedBy = urlHandle;
        }

        var text = embed.GetStringProp("description") ?? "";
        var ts = messageTimestamp;
        var tsRaw = embed.GetStringProp("timestamp");
        // Deleted tweets leave degenerate embeds with a 0001-01-01 timestamp — keep the
        // message timestamp for those.
        if (tsRaw is not null && DateTimeOffset.TryParse(tsRaw, out var parsed) && parsed.Year > 2000) ts = parsed;

        return new Post
        {
            Platform = Platform.Twitter,
            Handle = handle,
            DisplayName = displayName,
            Text = text,
            Url = canonicalUrl,
            Timestamp = ts.ToUniversalTime(),
            RepostedBy = repostedBy,
            Hashtags = HashtagExtractor.Extract(text),
            SourceFile = sourceFile,
            Channel = channel,
        };
    }
}

/// <summary>
/// Parses Bluesky posts out of Discord Components V2 blocks (SkyCord bot messages).
/// The DiscordLog format stores these as nested "components" with type-10 text nodes, e.g.:
///   **user** Just Posted On Bluesky
///   ### [**Name (@handle)**](https://bsky.app/profile/.../post/...)
///   post text...
///   **Quoting** [**Name (@handle)**](https://bsky.app/profile/...)
///   quoted text...
///   -# Reposted By user
///   -# &lt;:bsky:...&gt; Bluesky • &lt;t:1782985063:f&gt;
/// </summary>
public static partial class BlueskyComponentParser
{
    [GeneratedRegex(@"\[\*\*(?<name>.+?)\s+\(@(?<handle>[^)\s]+)\)\*\*\]\(<?(?<url>https://bsky\.app/profile/[^)>\s]+/post/[^)>\s]+)>?\)")]
    private static partial Regex PostHeader();

    [GeneratedRegex(@"\[\*\*(?<name>.+?)\s+\(@(?<handle>[^)\s]+)\)\*\*\]\(<?(?<url>https://bsky\.app/profile/[^)>\s]+)>?\)")]
    private static partial Regex ProfileLink();

    [GeneratedRegex(@"-#\s*Reposted By\s+(\S+)")]
    private static partial Regex RepostedBy();

    [GeneratedRegex(@"<t:(\d+)(?::[a-zA-Z])?>")]
    private static partial Regex UnixTimestamp();

    public static List<Post> Parse(JsonElement message, DateTimeOffset messageTimestamp, string sourceFile, string channel)
    {
        var lines = new List<string>();
        if (message.TryGetProperty("components", out var comps))
            FlattenTextComponents(comps, lines);
        if (lines.Count == 0) return new List<Post>();

        // Split multi-line component contents into individual lines, preserving order.
        var flat = lines.SelectMany(l => l.Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var posts = new List<Post>();
        Post? current = null;
        var text = new List<string>();
        var quotedText = new List<string>();
        bool inQuote = false;

        void Commit()
        {
            if (current is null) return;
            current.Text = string.Join("\n", text).Trim();
            current.QuotedText = quotedText.Count > 0 ? string.Join("\n", quotedText).Trim() : null;
            current.Hashtags = HashtagExtractor.Extract(current.Text, current.QuotedText);
            posts.Add(current);
            current = null;
            text.Clear();
            quotedText.Clear();
            inQuote = false;
        }

        foreach (var line in flat)
        {
            var header = PostHeader().Match(line);
            if (header.Success && (line.StartsWith("###") || current is null))
            {
                Commit();
                var url = header.Groups["url"].Value;
                var frag = url.IndexOf('#');
                current = new Post
                {
                    Platform = Platform.Bluesky,
                    Handle = header.Groups["handle"].Value,
                    DisplayName = header.Groups["name"].Value.Trim('*', ' '),
                    Url = frag >= 0 ? url[..frag] : url,
                    Timestamp = messageTimestamp.ToUniversalTime(),
                    SourceFile = sourceFile,
                    Channel = channel,
                };
                continue;
            }

            if (current is null) continue; // preamble like "**x** Just Posted On Bluesky"

            if (line.StartsWith("**Quoting**"))
            {
                inQuote = true;
                continue;
            }

            if (line.StartsWith("-#"))
            {
                var rb = RepostedBy().Match(line);
                if (rb.Success) current.RepostedBy = rb.Groups[1].Value.TrimStart('@');
                var ut = UnixTimestamp().Match(line);
                if (ut.Success)
                    current.Timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(ut.Groups[1].Value));
                continue;
            }

            if (inQuote)
            {
                var profile = ProfileLink().Match(line);
                if (profile.Success && current.QuotedHandle is null)
                    current.QuotedHandle = profile.Groups["handle"].Value;
                else
                    quotedText.Add(line);
            }
            else
            {
                text.Add(line);
            }
        }
        Commit();
        return posts;
    }

    private static void FlattenTextComponents(JsonElement element, List<string> lines)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                FlattenTextComponents(child, lines);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        if (element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number
            && t.GetInt32() == 10 && element.GetStringProp("content") is { } content)
            lines.Add(content);

        if (element.TryGetProperty("components", out var nested))
            FlattenTextComponents(nested, lines);
    }
}

public static class JsonExtensions
{
    public static string? GetStringProp(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
