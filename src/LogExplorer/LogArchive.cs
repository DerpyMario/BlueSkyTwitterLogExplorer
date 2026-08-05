using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace LogExplorer;

/// <summary>
/// Loads every export under a directory (recursively), including files inside .7z and .zip
/// archives — read as streams straight out of the archive, never extracted to disk. Parses the
/// JSON exports, merges duplicate copies of the same export file, and de-duplicates posts
/// across overlapping export ranges and formats.
/// </summary>
public sealed partial class LogArchive
{
    public List<ExportFile> Files { get; } = new();
    public List<Post> Posts { get; } = new();

    /// <summary>Total duplicate file copies merged away (same name+size, or same channel+dates).</summary>
    public int MergedFileCount => Files.Sum(f => f.MergedCount);

    private readonly HashSet<string> _postKeys = new();
    private readonly Dictionary<string, ExportFile> _byNameAndSize = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] LooseExtensions = { ".json", ".csv", ".txt", ".md", ".html", ".htm" };
    private static readonly string[] ArchiveExtensions = { ".7z", ".zip" };

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2}T\d{2}_\d{2}_\d{2}\.\d+Z)_to_(\d{4}-\d{2}-\d{2}T\d{2}_\d{2}_\d{2}\.\d+Z)")]
    private static partial Regex FileNameRange();

    [GeneratedRegex(@"\(after (\d{4}-\d{2}-\d{2})\)")]
    private static partial Regex FileNameAfter();

    public static LogArchive Load(string directory)
    {
        var archive = new LogArchive();
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var path in files)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ArchiveExtensions.Contains(ext))
            {
                archive.ScanCompressedArchive(path);
            }
            else if (LooseExtensions.Contains(ext))
            {
                archive.AddFile(path, container: null, new FileInfo(path).Length,
                    openStream: () => File.OpenRead(path));
            }
        }

        archive.Posts.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return archive;
    }

    /// <summary>Enumerates a .7z/.zip and parses supported entries from their streams (no extraction).</summary>
    private void ScanCompressedArchive(string archivePath)
    {
        var containerName = Path.GetFileName(archivePath);
        try
        {
            using var stream = File.OpenRead(archivePath);
            using var archive = ArchiveFactory.OpenArchive(stream);
            if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
            {
                // Forward-only reader: decompresses solid .7z blocks sequentially instead of
                // re-decompressing per entry.
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    var entry = reader.Entry;
                    if (entry.IsDirectory || entry.Key is null) continue;
                    if (!LooseExtensions.Contains(Path.GetExtension(entry.Key).ToLowerInvariant())) continue;

                    // The reader is forward-only, so the entry must be consumed here and once.
                    using var entryStream = reader.OpenEntryStream();
                    AddFile($"{containerName}::{entry.Key}", containerName, entry.Size, () => entryStream);
                }
            }
            else
            {
                // Zip supports random access per entry.
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory || entry.Key is null) continue;
                    if (!LooseExtensions.Contains(Path.GetExtension(entry.Key).ToLowerInvariant())) continue;
                    AddFile($"{containerName}::{entry.Key}", containerName, entry.Size, entry.OpenEntryStream);
                }
            }
        }
        catch (Exception ex)
        {
            Files.Add(new ExportFile
            {
                Path = archivePath,
                Container = containerName,
                Format = ExportFormat.Unknown,
                Error = $"cannot read archive: {ex.Message}",
            });
        }
    }

    private void AddFile(string path, string? container, long size, Func<Stream> openStream)
    {
        var file = new ExportFile { Path = path, Container = container, Size = size };
        var ext = Path.GetExtension(file.Name).ToLowerInvariant();
        file.Format = ext switch
        {
            ".json" => ExportFormat.Unknown, // refined during parse
            ".csv" => ExportFormat.Csv,
            ".txt" => ExportFormat.Txt,
            ".md" => ExportFormat.Markdown,
            ".html" or ".htm" => ExportFormat.Html,
            _ => ExportFormat.Unknown,
        };

        // Merge rule 1: an identical copy (same file name and uncompressed size) was already
        // loaded — e.g. the same export sitting in several archives. Skip it entirely.
        var identityKey = $"{file.Name}|{size}";
        if (_byNameAndSize.TryGetValue(identityKey, out var original))
        {
            original.MergedCount++;
            return;
        }

        GuessRangeFromName(file);
        file.Platform = GuessPlatform(file.Name);

        if (ext == ".json")
        {
            try
            {
                using var stream = openStream();
                ParseJson(file, stream);
            }
            catch (Exception ex)
            {
                file.Error = ex.Message;
            }

            // Merge rule 2: same channel and same export dates as an already-parsed file, and
            // every post in it was already known — a re-export of the same range under a
            // different name. Fold it into the earlier file instead of listing it.
            if (file.Error is null && file.ParsedPosts == 0 && file.MessageCount > 0)
            {
                var twin = Files.FirstOrDefault(f =>
                    f.ParsedPosts > 0
                    && f.Channel == file.Channel
                    && f.RangeFrom == file.RangeFrom
                    && f.RangeTo == file.RangeTo);
                if (twin is not null)
                {
                    twin.MergedCount++;
                    _byNameAndSize[identityKey] = twin;
                    return;
                }
            }
        }

        _byNameAndSize[identityKey] = file;
        Files.Add(file);
    }

    private void ParseJson(ExportFile file, Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { file.Error = "not an export file"; return; }

        string channel;
        if (root.TryGetProperty("channelName", out _))
        {
            // DiscordLog format
            file.Format = ExportFormat.DiscordLogJson;
            channel = root.GetStringProp("channelName") ?? "";
            if (DateTimeOffset.TryParse(root.GetStringProp("fromDate"), out var from)) file.RangeFrom = from;
            if (DateTimeOffset.TryParse(root.GetStringProp("toDate"), out var to)) file.RangeTo = to;
        }
        else if (root.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.Object)
        {
            // DiscordChatExporter format
            file.Format = ExportFormat.DiscordChatExporterJson;
            channel = ch.GetStringProp("name") ?? "";
            if (root.TryGetProperty("dateRange", out var dr) && dr.ValueKind == JsonValueKind.Object)
            {
                if (DateTimeOffset.TryParse(dr.GetStringProp("after"), out var from)) file.RangeFrom = from;
                if (DateTimeOffset.TryParse(dr.GetStringProp("before"), out var to)) file.RangeTo = to;
            }
        }
        else
        {
            file.Error = "unrecognized JSON layout";
            return;
        }

        file.Channel = channel;
        file.Platform = GuessPlatform(channel) is Platform.Unknown ? file.Platform : GuessPlatform(channel);

        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return;

        DateTimeOffset minTs = DateTimeOffset.MaxValue, maxTs = DateTimeOffset.MinValue;
        foreach (var message in messages.EnumerateArray())
        {
            file.MessageCount++;
            if (!DateTimeOffset.TryParse(message.GetStringProp("timestamp"), out var msgTs))
                continue;
            if (msgTs < minTs) minTs = msgTs;
            if (msgTs > maxTs) maxTs = msgTs;

            foreach (var post in ParseMessage(message, msgTs, file.Name, channel))
            {
                if (!_postKeys.Add(post.DedupeKey)) continue;
                Posts.Add(post);
                file.ParsedPosts++;
            }
        }

        // Fall back to observed message timestamps when the file itself carries no range metadata.
        file.RangeFrom ??= minTs == DateTimeOffset.MaxValue ? null : minTs;
        file.RangeTo ??= maxTs == DateTimeOffset.MinValue ? null : maxTs;
    }

    private static IEnumerable<Post> ParseMessage(JsonElement message, DateTimeOffset msgTs, string sourceFile, string channel)
    {
        // Tweets arrive as embeds (both export formats use the same field names).
        if (message.TryGetProperty("embeds", out var embeds) && embeds.ValueKind == JsonValueKind.Array)
        {
            foreach (var embed in embeds.EnumerateArray())
            {
                if (TweetEmbedParser.IsStatusUrl(embed.GetStringProp("url"))
                    && TweetEmbedParser.Parse(embed, msgTs, sourceFile, channel) is { } tweet)
                    yield return tweet;
            }
        }

        // Bluesky posts arrive as Components V2 blocks (DiscordLog format only —
        // DiscordChatExporter does not capture component content).
        foreach (var post in BlueskyComponentParser.Parse(message, msgTs, sourceFile, channel))
            yield return post;
    }

    private static void GuessRangeFromName(ExportFile file)
    {
        var m = FileNameRange().Match(file.Name);
        if (m.Success)
        {
            if (DateTimeOffset.TryParse(m.Groups[1].Value.Replace('_', ':'), out var from)) file.RangeFrom = from;
            if (DateTimeOffset.TryParse(m.Groups[2].Value.Replace('_', ':'), out var to)) file.RangeTo = to;
            return;
        }
        var a = FileNameAfter().Match(file.Name);
        if (a.Success && DateTimeOffset.TryParse(a.Groups[1].Value + "T00:00:00Z", out var after))
            file.RangeFrom = after;
    }

    private static Platform GuessPlatform(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("twitter") || lower.Contains("x-posts")) return Platform.Twitter;
        if (lower.Contains("blesky") || lower.Contains("bluesky") || lower.Contains("bsky")) return Platform.Bluesky;
        return Platform.Unknown;
    }

    public IEnumerable<UserStat> BuildUserStats(IEnumerable<Post> posts)
    {
        var stats = new Dictionary<(Platform, string), UserStat>();
        foreach (var post in posts)
        {
            var key = (post.Platform, post.HandleKey);
            if (!stats.TryGetValue(key, out var stat))
            {
                stat = new UserStat { Platform = post.Platform, Handle = post.Handle };
                stats[key] = stat;
            }
            stat.PostCount++;
            if (post.RepostedBy is not null) stat.RepostCount++;
            if (post.Timestamp < stat.FirstSeen) stat.FirstSeen = post.Timestamp;
            if (post.Timestamp >= stat.LastSeen)
            {
                stat.LastSeen = post.Timestamp;
                stat.DisplayName = post.DisplayName;
            }
            foreach (var tag in post.Hashtags)
                stat.HashtagCounts[HashtagExtractor.Normalize(tag)] =
                    stat.HashtagCounts.GetValueOrDefault(HashtagExtractor.Normalize(tag)) + 1;
            foreach (var cat in post.Categories)
                stat.CategoryCounts[cat] = stat.CategoryCounts.GetValueOrDefault(cat) + 1;
        }
        return stats.Values;
    }
}
