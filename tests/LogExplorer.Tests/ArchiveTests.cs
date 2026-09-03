using System.IO.Compression;
using LogExplorer;

namespace LogExplorer.Tests;

public class ArchiveTests
{
    private static string SampleDataDir =>
        Path.Combine(AppContext.BaseDirectory, "sample-data");

    [Fact]
    public void ReadsExportsInsideSevenZipWithoutExtracting()
    {
        // sample-data holds loose JSON exports plus sample-archive.7z containing identical
        // copies of two of them — the .7z entries must parse and then merge with the loose files.
        var archive = LogArchive.Load(SampleDataDir);

        Assert.Equal(2, archive.MergedFileCount);
        var merged = archive.Files.Where(f => f.MergedCount > 0).ToList();
        Assert.Equal(2, merged.Count);
        Assert.All(merged, f => Assert.Equal(1, f.MergedCount));
        // Nothing was double-counted: every post is unique across the loose and archived copies.
        Assert.Equal(archive.Files.Sum(f => f.ParsedPosts), archive.Posts.Count);
        Assert.Equal(archive.Posts.Select(p => p.DedupeKey).Distinct().Count(), archive.Posts.Count);
    }

    [Fact]
    public void ReadsExportsInsideZipWithoutExtracting()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"logexplorer-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var zipPath = Path.Combine(dir, "exports.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(
                    Path.Combine(SampleDataDir, "general-x-twitter-posts_sample.json"),
                    "general-x-twitter-posts_sample.json");
                zip.CreateEntryFromFile(
                    Path.Combine(SampleDataDir, "pov-in-general-justincase-blesky_sample.json"),
                    "nested/pov-in-general-justincase-blesky_sample.json");
            }

            var archive = LogArchive.Load(dir);
            Assert.Equal(5, archive.Posts.Count);
            Assert.Equal(2, archive.Files.Count);
            Assert.All(archive.Files, f => Assert.Equal("exports.zip", f.Container));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MergesRenamedDuplicateWithSameChannelAndDates()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"logexplorer-dup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(SampleDataDir, "general-x-twitter-posts_sample.json");
            File.Copy(source, Path.Combine(dir, "aaa-original.json"));
            // Renamed and re-indented: different name AND different size, but the same channel,
            // the same fromDate/toDate and no posts that are not already known → merged.
            var reIndented = System.Text.Json.JsonSerializer.Serialize(
                System.Text.Json.JsonDocument.Parse(File.ReadAllText(source)).RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(dir, "bbb-re-export.json"), reIndented);

            var archive = LogArchive.Load(dir);
            Assert.Equal(3, archive.Posts.Count);
            var file = Assert.Single(archive.Files);
            Assert.Equal("aaa-original.json", file.Name);
            Assert.Equal(1, file.MergedCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UnreadableArchiveIsReportedNotFatal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"logexplorer-bad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "corrupt.7z"), new byte[] { 1, 2, 3, 4 });
            var archive = LogArchive.Load(dir);
            var file = Assert.Single(archive.Files);
            Assert.NotNull(file.Error);
            Assert.Empty(archive.Posts);
        }
        finally { Directory.Delete(dir, true); }
    }
}
