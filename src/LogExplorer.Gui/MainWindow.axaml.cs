using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace LogExplorer.Gui;

public sealed record PostRow(string Header, string Text, string Quoted, string Url, bool HasQuote);
public sealed record UserRow(string Platform, string Handle, string Name, int Posts, int Reposts, string Seen, string TopTags);
public sealed record TagRow(int Count, string Tag, string Categories, string RawTag);
public sealed record CategoryRow(string Title, string Tags, string TopUsers);
public sealed record LabelRow(int Posts, int Users, string Kind, string Name, string Seen, string Why, string Key);
public sealed record FileRow(string Format, string Platform, string Range, int Messages, int Posts, string Name, string Notes);

public partial class MainWindow : Window
{
    private const int MaxPostRows = 1000;

    private LogArchive? _archive;
    private LuaFilterEngine? _lua;
    private LabelEngine? _labels;
    private List<Post> _filtered = new();
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        DataDirBox.Text = Directory.Exists("BlueSkyX")
            ? Path.GetFullPath("BlueSkyX")
            : Environment.GetEnvironmentVariable("LOGEXPLORER_DATA") ?? "";
        if (DataDirBox.Text.Length > 0)
            Loaded += (_, _) => _ = LoadArchiveAsync(DataDirBox.Text!);
    }

    // ---- data loading -------------------------------------------------------

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick the folder with the DiscordLog exports (.7z/.zip or extracted)",
            AllowMultiple = false,
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            DataDirBox.Text = path;
            await LoadArchiveAsync(path);
        }
    }

    private async void OnLoad(object? sender, RoutedEventArgs e)
    {
        var dir = DataDirBox.Text?.Trim();
        if (string.IsNullOrEmpty(dir)) { LoadInfo.Text = "pick a data folder first"; return; }
        await LoadArchiveAsync(dir);
    }

    private async Task LoadArchiveAsync(string dir)
    {
        if (_busy) return;
        if (!Directory.Exists(dir)) { LoadInfo.Text = $"folder not found: {dir}"; return; }
        _busy = true;
        LoadButton.IsEnabled = ApplyButton.IsEnabled = false;
        LoadInfo.Text = "loading…";
        try
        {
            var filtersPath = FindFiltersScript();
            var (archive, lua, labels) = await Task.Run(() =>
            {
                var a = LogArchive.Load(dir);
                var l = new LuaFilterEngine(filtersPath);
                foreach (var post in a.Posts)
                    l.AssignCategories(post);
                var detected = LabelEngine.Build(a.Posts, l);
                return (a, l, detected);
            });

            _archive = archive;
            _lua = lua;
            _labels = labels;
            LoadInfo.Text =
                $"{archive.Files.Count} files ({archive.MergedFileCount} duplicates merged) — " +
                $"{archive.Posts.Count} posts ({archive.Posts.Count(p => p.Platform == Platform.Twitter)} tw / " +
                $"{archive.Posts.Count(p => p.Platform == Platform.Bluesky)} bs) — " +
                $"{labels.Labels.Count} subjects detected";

            KindBox.ItemsSource = new[] { "All kinds" }
                .Concat(labels.Labels.Select(l => l.Kind).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                .ToList();
            KindBox.SelectedIndex = 0;

            CategoryBox.ItemsSource =
                new[] { "All categories" }
                .Concat(lua.Categories.Select(c => c.Name))
                .Append(lua.UncategorizedName)
                .ToList();
            CategoryBox.SelectedIndex = 0;

            PopulateFilesTab(archive);
            ApplyFilters();
        }
        catch (Exception ex)
        {
            LoadInfo.Text = $"error: {ex.Message}";
        }
        finally
        {
            _busy = false;
            LoadButton.IsEnabled = ApplyButton.IsEnabled = true;
        }
    }

    private static string FindFiltersScript()
    {
        var candidates = new[]
        {
            "scripts/filters.lua",
            Path.Combine(AppContext.BaseDirectory, "scripts", "filters.lua"),
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("scripts/filters.lua not found next to the app");
    }

    // ---- filtering ----------------------------------------------------------

    private void OnApply(object? sender, RoutedEventArgs e) => ApplyFilters();

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        PlatformBox.SelectedIndex = 0;
        if (CategoryBox.ItemCount > 0) CategoryBox.SelectedIndex = 0;
        if (KindBox.ItemCount > 0) KindBox.SelectedIndex = 0;
        TagBox.Text = UserBox.Text = BetweenBox.Text = SearchBox.Text = WhereBox.Text = LabelBox.Text = "";
        FromDate.SelectedDate = ToDate.SelectedDate = null;
        NoRepostsBox.IsChecked = OnlyRepostsBox.IsChecked = false;
        ApplyFilters();
    }

    private FilterOptions BuildOptions()
    {
        var o = new FilterOptions
        {
            Platform = PlatformBox.SelectedIndex switch
            {
                1 => LogExplorer.Platform.Twitter,
                2 => LogExplorer.Platform.Bluesky,
                _ => null,
            },
            ExcludeReposts = NoRepostsBox.IsChecked == true,
            OnlyReposts = OnlyRepostsBox.IsChecked == true,
        };
        if (!string.IsNullOrWhiteSpace(TagBox.Text))
            o.Tags = TagBox.Text.Split(',').Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0).ToList();
        if (!string.IsNullOrWhiteSpace(UserBox.Text))
            o.Users = UserBox.Text.Split(',').Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
        if (CategoryBox.SelectedIndex > 0 && CategoryBox.SelectedItem is string cat)
            o.Categories = new List<string> { cat };
        if (KindBox.SelectedIndex > 0 && KindBox.SelectedItem is string kind)
            o.Kinds = new List<string> { kind };
        if (!string.IsNullOrWhiteSpace(LabelBox.Text))
            o.Labels = LabelBox.Text.Split(',').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (FromDate.SelectedDate is { } from)
            o.From = new DateTimeOffset(DateTime.SpecifyKind(from.Date, DateTimeKind.Utc));
        if (ToDate.SelectedDate is { } to)
            o.To = new DateTimeOffset(DateTime.SpecifyKind(to.Date, DateTimeKind.Utc)).AddDays(1).AddTicks(-1);
        if (!string.IsNullOrWhiteSpace(BetweenBox.Text))
        {
            var parts = BetweenBox.Text.Split('-', 2);
            if (parts.Length == 2 && TimeSpan.TryParse(parts[0], out var lo) && TimeSpan.TryParse(parts[1], out var hi))
            {
                o.TimeFrom = lo;
                o.TimeTo = hi;
            }
        }
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) o.Contains = SearchBox.Text;
        if (!string.IsNullOrWhiteSpace(WhereBox.Text)) o.WhereLua = WhereBox.Text;
        o.ApplyLuaDefaults(_lua!);
        return o;
    }

    private void ApplyFilters()
    {
        if (_archive is null || _lua is null) return;
        try
        {
            _filtered = PostFilter.Apply(_archive.Posts, BuildOptions(), _lua);
            StatusText.Text = $"{_filtered.Count:N0} posts match" +
                              (_filtered.Count > MaxPostRows ? $" (showing first {MaxPostRows:N0})" : "");
            PopulatePostsTab();
            PopulateUsersTab();
            PopulateTagsTab();
            PopulateLabelsTab();
            PopulateCategoriesTab();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"filter error: {ex.Message}";
        }
    }

    // ---- tab population -----------------------------------------------------

    private void PopulatePostsTab()
    {
        PostsList.ItemsSource = _filtered.Take(MaxPostRows).Select(p =>
        {
            var repost = p.RepostedBy is null ? "" : $"  ⇄ reposted by @{p.RepostedBy}";
            var cats = p.Categories.Count > 0 ? $"  [{string.Join(", ", p.Categories)}]" : "";
            var subjects = p.Labels.Count > 0
                ? $"  {{{string.Join(", ", p.Labels)}{(p.Kinds.Count > 0 ? " · " + string.Join("/", p.Kinds) : "")}}}"
                : "";
            var tag = p.Platform == LogExplorer.Platform.Twitter ? "🐦" : "🦋";
            return new PostRow(
                $"{p.Timestamp:yyyy-MM-dd HH:mm} {tag} @{p.Handle} ({p.DisplayName}){repost}{cats}{subjects}",
                p.Text.Length > 0 ? p.Text : "(no text)",
                p.QuotedHandle is null ? "" : $"↳ quoting @{p.QuotedHandle}: {p.QuotedText}",
                p.Url,
                p.QuotedHandle is not null);
        }).ToList();
    }

    private void PopulateUsersTab()
    {
        UsersList.ItemsSource = _archive!.BuildUserStats(_filtered)
            .OrderByDescending(s => s.PostCount)
            .Select(s => new UserRow(
                s.Platform.ToString(), "@" + s.Handle, s.DisplayName, s.PostCount, s.RepostCount,
                $"{s.FirstSeen:yyyy-MM-dd} → {s.LastSeen:yyyy-MM-dd}",
                string.Join(" ", s.HashtagCounts.OrderByDescending(kv => kv.Value).Take(5).Select(kv => "#" + kv.Key))))
            .ToList();
    }

    private void PopulateTagsTab()
    {
        var counts = new Dictionary<string, (int Count, string Display)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _filtered)
            foreach (var tag in p.Hashtags)
            {
                var key = HashtagExtractor.Normalize(tag);
                counts[key] = (counts.TryGetValue(key, out var c) ? c.Count + 1 : 1,
                               counts.TryGetValue(key, out var d) && d.Display.Length > 0 ? d.Display : tag);
            }

        TagsList.ItemsSource = counts
            .OrderByDescending(kv => kv.Value.Count)
            .Select(kv => new TagRow(
                kv.Value.Count,
                "#" + kv.Value.Display,
                string.Join(", ", _lua!.Categories
                    .Where(c => c.Tags.Any(ct => _lua.TagMatches(kv.Key, ct)))
                    .Select(c => c.Name)),
                kv.Key))
            .ToList();
    }

    private void PopulateLabelsTab()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _filtered)
            foreach (var name in p.Labels)
                counts[name] = counts.GetValueOrDefault(name) + 1;

        LabelsList.ItemsSource = _labels!.Labels
            .Where(l => counts.ContainsKey(l.Display))
            .OrderByDescending(l => counts[l.Display])
            .Select(l => new LabelRow(
                counts[l.Display], l.UserCount,
                (l.Inherited ? "~" : "") + l.Kind,
                l.Display,
                $"{l.FirstSeen:yyyy-MM-dd} → {l.LastSeen:yyyy-MM-dd}",
                l.Inherited
                    ? $"kind inherited from {string.Join(", ", l.Related.Take(3))}"
                    : l.Kind == _labels.Options.UnknownName
                        ? $"no vocabulary matched · spelled {string.Join(" ", l.Variants.Take(3))}"
                        : $"{l.Confidence:P0} of the evidence: {string.Join(", ", l.Evidence)} · spelled {string.Join(" ", l.Variants.Take(3))}",
                l.Key))
            .ToList();
    }

    private void OnLabelDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (LabelsList.SelectedItem is LabelRow row)
        {
            LabelBox.Text = row.Name;
            ApplyFilters();
            Tabs.SelectedIndex = 0;
        }
    }

    private void PopulateCategoriesTab()
    {
        var byCat = new Dictionary<string, List<Post>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _filtered)
            foreach (var c in p.Categories)
                (byCat.TryGetValue(c, out var list) ? list : byCat[c] = new List<Post>()).Add(p);

        var rows = _lua!.Categories.Select(cat =>
        {
            var matched = byCat.GetValueOrDefault(cat.Name) ?? new List<Post>();
            var users = matched.GroupBy(p => (p.Platform, p.HandleKey))
                .OrderByDescending(g => g.Count()).Take(5)
                .Select(g => $"@{g.First().Handle} ({g.Count()})");
            return new CategoryRow(
                $"{cat.Name} — {matched.Count} posts, {matched.DistinctBy(p => (p.Platform, p.HandleKey)).Count()} users",
                cat.Tags.Count > 0 ? "tags: " + string.Join(", ", cat.Tags.OrderBy(t => t).Select(t => "#" + t)) : "(no tag list)",
                matched.Count > 0 ? "top: " + string.Join(", ", users) : "");
        }).ToList();

        var uncat = byCat.GetValueOrDefault(_lua.UncategorizedName)?.Count ?? 0;
        rows.Add(new CategoryRow($"{_lua.UncategorizedName} — {uncat} posts", "", ""));
        CategoriesList.ItemsSource = rows;
    }

    private void PopulateFilesTab(LogArchive archive)
    {
        FilesList.ItemsSource = archive.Files.Select(f => new FileRow(
            f.Format switch
            {
                ExportFormat.DiscordLogJson => "DiscordLog JSON",
                ExportFormat.DiscordChatExporterJson => "DiscordChatExporter",
                _ => $"{f.Format} (listed only)",
            },
            f.Platform.ToString(),
            f.RangeFrom is null && f.RangeTo is null ? "-" : $"{f.RangeFrom:yyyy-MM-dd HH:mm} → {f.RangeTo:yyyy-MM-dd HH:mm}",
            f.MessageCount,
            f.ParsedPosts,
            f.Container is null ? f.Name : $"{f.Container} :: {f.Name}",
            (f.MergedCount > 0 ? $"+{f.MergedCount} duplicate merged " : "") + (f.Error is null ? "" : $"error: {f.Error}"))).ToList();
    }

    // ---- interactions -------------------------------------------------------

    private void OnUserDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (UsersList.SelectedItem is UserRow row)
        {
            UserBox.Text = row.Handle.TrimStart('@');
            ApplyFilters();
            Tabs.SelectedIndex = 0;
        }
    }

    private void OnTagDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (TagsList.SelectedItem is TagRow row)
        {
            TagBox.Text = row.RawTag;
            ApplyFilters();
            Tabs.SelectedIndex = 0;
        }
    }

    // ---- export -------------------------------------------------------------

    private void OnExportJson(object? sender, RoutedEventArgs e) => _ = ExportAsync("json");
    private void OnExportCsv(object? sender, RoutedEventArgs e) => _ = ExportAsync("csv");
    private void OnExportMd(object? sender, RoutedEventArgs e) => _ = ExportAsync("md");

    private async Task ExportAsync(string format)
    {
        if (_archive is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {_filtered.Count} posts as {format.ToUpperInvariant()}",
            SuggestedFileName = $"export.{format}",
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            try
            {
                await Task.Run(() => Exporters.Write(_filtered, path, format));
                StatusText.Text = $"wrote {_filtered.Count:N0} posts to {path}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"export failed: {ex.Message}";
            }
        }
    }
}
