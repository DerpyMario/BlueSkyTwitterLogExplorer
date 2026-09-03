using System.Text.Json.Serialization;

namespace LogExplorer;

public enum Platform
{
    Twitter,
    Bluesky,
    Unknown,
}

public enum ExportFormat
{
    DiscordLogJson,       // DiscordLog export: { serverName, channelName, fromDate, toDate, messages: [raw API msgs] }
    DiscordChatExporterJson, // DiscordChatExporter: { guild, channel, dateRange, messages: [...] }
    Csv,
    Txt,
    Markdown,
    Html,
    Unknown,
}

/// <summary>A single Bluesky or Twitter/X post recovered from the Discord log exports.</summary>
public sealed class Post
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Platform Platform { get; set; }

    /// <summary>Author handle without @ (e.g. "VAMichaelaLaws" or "ghaspey.bsky.social").</summary>
    public string Handle { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>Canonical URL of the post (fragment/query stripped, twitter.com normalized).</summary>
    public string Url { get; set; } = "";

    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Handle of the account that reposted/retweeted this post into the feed, if any.</summary>
    public string? RepostedBy { get; set; }

    /// <summary>Handle of the account being quoted, when the post quotes another post.</summary>
    public string? QuotedHandle { get; set; }

    public string? QuotedText { get; set; }

    /// <summary>Hashtags in original casing, extracted from Text + QuotedText (URLs excluded).</summary>
    public List<string> Hashtags { get; set; } = new();

    /// <summary>Categories assigned by the Lua filter script.</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>Subject names (games, shows, events, …) detected in this post, discovered from the
    /// data rather than from any hardcoded list.</summary>
    public List<string> Labels { get; set; } = new();

    /// <summary>What kinds of thing those labels are ("Video game", "Anime", …).</summary>
    public List<string> Kinds { get; set; } = new();

    public string SourceFile { get; set; } = "";

    public string Channel { get; set; } = "";

    [JsonIgnore]
    public string HandleKey => Handle.ToLowerInvariant();

    /// <summary>Key used to de-duplicate the same post appearing in overlapping exports.</summary>
    [JsonIgnore]
    public string DedupeKey =>
        $"{Platform}|{(Url.Length > 0 ? Url.ToLowerInvariant() : Handle.ToLowerInvariant() + "@" + Timestamp.ToUnixTimeSeconds())}|{RepostedBy?.ToLowerInvariant() ?? ""}";
}

/// <summary>Aggregated view of one Bluesky/Twitter user across all exports.</summary>
public sealed class UserStat
{
    public Platform Platform { get; init; }
    public string Handle { get; init; } = "";
    public string DisplayName { get; set; } = "";
    public int PostCount { get; set; }
    public int RepostCount { get; set; }
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.MaxValue;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.MinValue;
    public Dictionary<string, int> HashtagCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> CategoryCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One file found in the export directory — loose on disk or inside a .7z/.zip archive.</summary>
public sealed class ExportFile
{
    public string Path { get; init; } = "";

    public string Name
    {
        get
        {
            var p = Path.Replace('\\', '/');
            var sep = p.LastIndexOf("::", StringComparison.Ordinal);
            if (sep >= 0) p = p[(sep + 2)..];
            return System.IO.Path.GetFileName(p);
        }
    }

    /// <summary>Name of the .7z/.zip the file lives in, or null for loose files.</summary>
    public string? Container { get; init; }

    /// <summary>Uncompressed size in bytes (used to detect identical duplicate copies).</summary>
    public long Size { get; init; }

    /// <summary>How many identical/same-date duplicate copies were merged into this entry.</summary>
    public int MergedCount { get; set; }

    public ExportFormat Format { get; set; }
    public string Channel { get; set; } = "";
    public Platform Platform { get; set; } = Platform.Unknown;
    public DateTimeOffset? RangeFrom { get; set; }
    public DateTimeOffset? RangeTo { get; set; }
    public int MessageCount { get; set; }
    public int ParsedPosts { get; set; }
    public string? Error { get; set; }
}
