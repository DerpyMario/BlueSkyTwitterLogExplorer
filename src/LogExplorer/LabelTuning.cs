using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogExplorer;

/// <summary>One kind as the tuning file stores it.</summary>
public sealed class KindTuning
{
    public string Name { get; set; } = "";
    public double Weight { get; set; } = 1.0;
    public List<string> Terms { get; set; } = new();
}

/// <summary>
/// A saved snapshot of every label-detection dial, so the settings can be changed from the GUI
/// instead of by editing <c>filters.lua</c>. When the file exists it replaces the values the Lua
/// script provides; deleting it hands control back to the script. The CLI reads it too, so both
/// front ends always agree on how subjects were detected.
/// </summary>
public sealed class LabelTuning
{
    public const string FileName = "label-tuning.json";

    public int MinPosts { get; set; } = 3;
    public double MinConfidence { get; set; } = 0.05;
    public double MinMargin { get; set; } = 1.3;
    public int MinTerms { get; set; } = 2;
    public double AuthorWeight { get; set; } = 6.0;
    public int LearnedTermsPerKind { get; set; } = 40;
    public double LearnedWeight { get; set; } = 0.5;
    public bool Propagate { get; set; } = true;
    public double PropagateShare { get; set; } = 0.4;
    public bool ExcludeSignalWords { get; set; } = true;
    public string UnknownName { get; set; } = "Unclassified";
    public List<KindTuning> Kinds { get; set; } = new();

    [JsonIgnore]
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The tuning file that belongs to a given filters script.</summary>
    public static string PathFor(string filtersScriptPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filtersScriptPath)) ?? ".", FileName);

    public static LabelTuning? Load(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<LabelTuning>(File.ReadAllText(path), JsonOptions);
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>Reads the current settings out of the engine, ready to be edited and saved.</summary>
    public static LabelTuning CaptureFrom(LabelOptions o) => new()
    {
        MinPosts = o.MinPosts,
        MinConfidence = o.MinConfidence,
        MinMargin = o.MinMargin,
        MinTerms = o.MinTerms,
        AuthorWeight = o.AuthorWeight,
        LearnedTermsPerKind = o.LearnedTermsPerKind,
        LearnedWeight = o.LearnedWeight,
        Propagate = o.Propagate,
        PropagateShare = o.PropagateShare,
        ExcludeSignalWords = o.ExcludeSignalWords,
        UnknownName = o.UnknownName,
        Kinds = o.Kinds.Select(k => new KindTuning
        {
            Name = k.Name,
            Weight = k.Weight,
            Terms = k.Terms.ToList(),
        }).ToList(),
    };

    public void ApplyTo(LabelOptions o)
    {
        o.MinPosts = Math.Max(1, MinPosts);
        o.MinConfidence = MinConfidence;
        o.MinMargin = MinMargin;
        o.MinTerms = Math.Max(1, MinTerms);
        o.AuthorWeight = AuthorWeight;
        o.LearnedTermsPerKind = Math.Max(0, LearnedTermsPerKind);
        o.LearnedWeight = LearnedWeight;
        o.Propagate = Propagate;
        o.PropagateShare = PropagateShare;
        o.ExcludeSignalWords = ExcludeSignalWords;
        if (!string.IsNullOrWhiteSpace(UnknownName)) o.UnknownName = UnknownName.Trim();

        // An empty kind list would leave nothing to classify with, so it is ignored rather than
        // silently wiping the vocabulary that came from the script.
        var kinds = Kinds
            .Where(k => !string.IsNullOrWhiteSpace(k.Name) && k.Terms.Any(t => !string.IsNullOrWhiteSpace(t)))
            .Select(k => new LabelKind
            {
                Name = k.Name.Trim(),
                Weight = k.Weight,
                Terms = k.Terms.Select(t => t.Trim()).Where(t => t.Length > 0).ToList(),
            })
            .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (kinds.Count > 0) o.Kinds = kinds;
    }

    /// <summary>Applies the saved tuning for a filters script, if any. Returns the file it used.</summary>
    public static string? ApplySaved(string filtersScriptPath, LabelOptions options)
    {
        var path = PathFor(filtersScriptPath);
        var tuning = Load(path);
        if (tuning is null) return null;
        tuning.ApplyTo(options);
        return path;
    }
}
