using System.Text;
using System.Text.RegularExpressions;

namespace LogExplorer;

/// <summary>One kind of subject a label can belong to (e.g. "Video game"), described only by
/// generic vocabulary that tends to appear around such subjects — never by title names.</summary>
public sealed class LabelKind
{
    public string Name { get; init; } = "";
    public List<string> Terms { get; init; } = new();

    /// <summary>How much this kind's vocabulary counts. Kinds describing an *occasion* around a
    /// subject (a convention, a piece of fanart) deserve less than kinds describing what the
    /// subject actually is, because the occasion vocabulary surrounds everything.</summary>
    public double Weight { get; init; } = 1.0;
}

public sealed class LabelOptions
{
    /// <summary>How many posts a hashtag must appear in before it is treated as a label.</summary>
    public int MinPosts = 3;

    /// <summary>Share of a label's posts that must carry a kind's vocabulary to accept that kind.</summary>
    public double MinConfidence = 0.05;

    /// <summary>How far ahead of the runner-up the winning kind must be. Below this the label is
    /// left for co-occurrence to decide rather than guessed at.</summary>
    public double MinMargin = 1.3;

    /// <summary>Extra weight for posts written by the subject's own account — an account named after
    /// the subject describes what it is far more reliably than anyone talking about it.</summary>
    public double AuthorWeight = 6.0;

    /// <summary>How many *different* vocabulary words must back a decision. One ambiguous word
    /// ("chapter", "trailer") should not settle what a subject is.</summary>
    public int MinTerms = 2;

    /// <summary>Learn extra vocabulary for each kind from the subjects already classified, so the
    /// word lists do not have to anticipate how people actually talk. 0 disables learning.</summary>
    public int LearnedTermsPerKind = 40;

    /// <summary>How much a learned word counts next to a hand-written one.</summary>
    public double LearnedWeight = 0.5;

    /// <summary>A learned word must be this much more common in its kind than the kind's overall
    /// share of the corpus, and above <see cref="LearnedMinShare"/> in absolute terms.</summary>
    public double LearnedLift = 1.6;

    public double LearnedMinShare = 0.6;

    /// <summary>Minimum weighted occurrences before a word can be learned at all.</summary>
    public double LearnedMinCount = 6.0;

    /// <summary>Let unclassified labels inherit a kind from the labels they co-occur with.</summary>
    public bool Propagate = true;

    /// <summary>Share of co-occurrence weight one kind needs before it is inherited.</summary>
    public double PropagateShare = 0.5;

    /// <summary>Drop hashtags that are themselves generic vocabulary (#anime, #gamedev, …) —
    /// they describe a kind, they are not the name of a work.</summary>
    public bool ExcludeSignalWords = true;

    public string UnknownName = "Unclassified";

    public List<LabelKind> Kinds = new();
}

/// <summary>A subject name discovered in the logs (a game, a show, an event, …) together with the
/// kind it was classified as and the evidence that decided it.</summary>
public sealed class LabelInfo
{
    /// <summary>Normalized identity: lower-cased with separators removed.</summary>
    public string Key { get; init; } = "";

    /// <summary>Human-readable name, de-CamelCased from the most common spelling.</summary>
    public string Display { get; set; } = "";

    public string Kind { get; set; } = "";

    /// <summary>Share of the label's posts that carried the winning kind's vocabulary.</summary>
    public double Confidence { get; set; }

    /// <summary>True when the kind was inherited from co-occurring labels rather than direct evidence.</summary>
    public bool Inherited { get; set; }

    public int PostCount { get; set; }
    public int UserCount { get; set; }
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.MaxValue;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Original hashtag spellings that folded into this label.</summary>
    public List<string> Variants { get; } = new();

    /// <summary>The vocabulary terms that most often decided the kind.</summary>
    public List<string> Evidence { get; set; } = new();

    /// <summary>Labels most often seen in the same posts.</summary>
    public List<string> Related { get; set; } = new();
}

/// <summary>
/// Discovers subject labels (names of games, shows, events, …) from the posts themselves and works
/// out what kind of thing each one is.
///
/// Nothing about the labels is hardcoded: names are mined from the hashtags actually present in the
/// logs, and a label's kind is decided by which *generic* vocabulary (configured in Lua — words like
/// "episode", "patch notes", "wishlist") shows up in the posts that use it. Labels that carry no such
/// evidence can still be classified by inheriting from the labels they keep company with.
/// </summary>
public sealed partial class LabelEngine
{
    public List<LabelInfo> Labels { get; } = new();
    public Dictionary<string, LabelInfo> ByKey { get; } = new(StringComparer.OrdinalIgnoreCase);
    public LabelOptions Options { get; }

    /// <summary>Kind names that ended up in use, ordered by how many posts they cover.</summary>
    public List<string> KindNames { get; private set; } = new();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"[_\-. ]")]
    private static partial Regex Separators();

    [GeneratedRegex(@"<?https?://\S+")]
    private static partial Regex LinkTarget();

    private LabelEngine(LabelOptions options) => Options = options;

    public static string NormalizeKey(string tag) =>
        Separators().Replace(tag.TrimStart('#'), "").ToLowerInvariant();

    public static LabelEngine Build(IReadOnlyList<Post> posts, LuaFilterEngine lua)
    {
        var engine = new LabelEngine(lua.LabelOptions);
        engine.Discover(posts);
        engine.Classify(posts, lua);
        engine.Annotate(posts);
        return engine;
    }

    // ---- 1. discover candidate labels from the hashtags actually in the data ----------------

    private readonly Dictionary<string, Dictionary<string, int>> _variantCounts = new(StringComparer.OrdinalIgnoreCase);

    private void Discover(IReadOnlyList<Post> posts)
    {
        var postCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var users = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var first = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var last = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        // Generic vocabulary describes a kind, so a hashtag that *is* such a word is not a name.
        var vocabulary = Options.ExcludeSignalWords
            ? Options.Kinds.SelectMany(k => k.Terms).Select(NormalizeKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var post in posts)
        {
            foreach (var key in post.Hashtags.Select(NormalizeKey).Where(k => k.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (vocabulary.Contains(key)) continue;
                postCounts[key] = postCounts.GetValueOrDefault(key) + 1;
                (users.TryGetValue(key, out var set) ? set : users[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    .Add(post.HandleKey);
                if (!first.TryGetValue(key, out var f) || post.Timestamp < f) first[key] = post.Timestamp;
                if (!last.TryGetValue(key, out var l) || post.Timestamp > l) last[key] = post.Timestamp;
            }
            foreach (var tag in post.Hashtags)
            {
                var key = NormalizeKey(tag);
                if (key.Length <= 1 || vocabulary.Contains(key)) continue;
                var variants = _variantCounts.TryGetValue(key, out var v) ? v : _variantCounts[key] = new Dictionary<string, int>();
                variants[tag] = variants.GetValueOrDefault(tag) + 1;
            }
        }

        foreach (var (key, count) in postCounts)
        {
            if (count < Options.MinPosts) continue;
            var info = new LabelInfo
            {
                Key = key,
                Display = PrettyName(_variantCounts[key]),
                Kind = Options.UnknownName,
                PostCount = count,
                UserCount = users[key].Count,
                FirstSeen = first[key],
                LastSeen = last[key],
            };
            info.Variants.AddRange(_variantCounts[key].OrderByDescending(kv => kv.Value).Select(kv => "#" + kv.Key));
            Labels.Add(info);
            ByKey[key] = info;
        }
    }

    /// <summary>Turns the most common spelling of a hashtag into a readable name
    /// ("GenshinImpact" → "Genshin Impact", "witch_hat_atelier" → "witch hat atelier").</summary>
    internal static string PrettyName(Dictionary<string, int> variants)
    {
        // Among the spellings people actually use, prefer one that shows the word breaks:
        // "DaemonsOfTheShadowRealm" reads better than "daemonsoftheshadowrealm".
        var top = variants.Max(kv => kv.Value);
        var best = variants
            .Where(kv => kv.Value >= top * 0.4)
            .OrderByDescending(kv => SplitWords(kv.Key).Count(c => c == ' '))
            .ThenByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .First().Key;
        return SplitWords(best);
    }

    public static string SplitWords(string tag)
    {
        tag = tag.Replace('_', ' ').Replace('-', ' ');
        // Only split scripts that actually use casing; Japanese and friends stay untouched.
        if (!tag.Any(char.IsUpper)) return tag;

        var sb = new StringBuilder(tag.Length + 8);
        for (int i = 0; i < tag.Length; i++)
        {
            var c = tag[i];
            if (i > 0 && char.IsUpper(c) && tag[i - 1] != ' ')
            {
                var prev = tag[i - 1];
                var nextIsLower = i + 1 < tag.Length && char.IsLower(tag[i + 1]);
                // "GenshinImpact" → after a lower/digit; "AXCosplay" → last capital of a run.
                if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && nextIsLower))
                    sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- 2. classify each label by the vocabulary found in its posts ------------------------

    /// <summary>The words that speak for each kind, and how much each word is trusted.</summary>
    private sealed class Vocabulary
    {
        public Dictionary<string, List<int>> Tokens = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Term, int Kind)> Phrases = new();
        public Dictionary<string, double> Trust = new(StringComparer.OrdinalIgnoreCase);
        public List<HashSet<string>> OfKind = new();
    }

    /// <summary>What the corpus had to say about each label.</summary>
    private sealed class Evidence
    {
        public Dictionary<string, Dictionary<string, double>> Bags = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Posts = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Authoritative = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> TermPosts = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, int>> Cooccurrence = new(StringComparer.OrdinalIgnoreCase);
    }

    private void Classify(IReadOnlyList<Post> posts, LuaFilterEngine lua)
    {
        var kinds = Options.Kinds;
        if (kinds.Count == 0) return;

        // Round one: only the hand-written vocabulary, which is small but trustworthy.
        var vocabulary = BuildVocabulary(kinds, learned: null);
        var evidence = Collect(posts, vocabulary);
        Decide(evidence, vocabulary, kinds);

        // Round two: having seen which subjects are confidently what, learn how people actually
        // talk about each kind and judge everything again with the richer vocabulary. This is what
        // keeps the word lists from having to anticipate every phrase a community uses.
        if (Options.LearnedTermsPerKind > 0)
        {
            var learned = Learn(posts, kinds, vocabulary);
            if (learned.Any(l => l.Count > 0))
            {
                foreach (var label in Labels)
                {
                    label.Kind = Options.UnknownName;
                    label.Confidence = 0;
                    label.Evidence = new List<string>();
                }
                vocabulary = BuildVocabulary(kinds, learned);
                evidence = Collect(posts, vocabulary);
                Decide(evidence, vocabulary, kinds);
            }
        }

        foreach (var label in Labels)
            if (evidence.Cooccurrence.TryGetValue(label.Key, out var related))
                label.Related = related.OrderByDescending(kv => kv.Value)
                    .Where(kv => ByKey.ContainsKey(kv.Key)).Take(5).Select(kv => ByKey[kv.Key].Display).ToList();

        if (Options.Propagate)
        {
            Propagate(evidence.Cooccurrence);
            Propagate(evidence.Cooccurrence); // a second round lets newly-decided labels help neighbours
        }

        // Let Lua have the final word on any label, without naming any of them in C#.
        foreach (var label in Labels)
            if (lua.DetectLabelKind(label) is { } custom && custom.Length > 0)
            {
                label.Kind = custom;
                label.Inherited = false;
            }

        KindNames = Labels.Where(l => l.Kind != Options.UnknownName)
            .GroupBy(l => l.Kind).OrderByDescending(g => g.Sum(l => l.PostCount))
            .Select(g => g.Key).ToList();
    }

    private Vocabulary BuildVocabulary(List<LabelKind> kinds, List<List<string>>? learned)
    {
        var vocabulary = new Vocabulary();
        void Add(string raw, int kind, double trust)
        {
            var term = raw.Trim().ToLowerInvariant();
            if (term.Length == 0) return;
            vocabulary.Trust[term] = Math.Max(vocabulary.Trust.GetValueOrDefault(term), trust);
            vocabulary.OfKind[kind].Add(term);
            if (term.All(c => char.IsLetterOrDigit(c) && c < 128))
            {
                var list = vocabulary.Tokens.TryGetValue(term, out var l) ? l : vocabulary.Tokens[term] = new List<int>();
                if (!list.Contains(kind)) list.Add(kind);
            }
            else if (!vocabulary.Phrases.Contains((term, kind)))
            {
                vocabulary.Phrases.Add((term, kind));
            }
        }

        for (int k = 0; k < kinds.Count; k++) vocabulary.OfKind.Add(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        for (int k = 0; k < kinds.Count; k++)
            foreach (var term in kinds[k].Terms) Add(term, k, 1.0);
        if (learned is not null)
            for (int k = 0; k < kinds.Count && k < learned.Count; k++)
                foreach (var term in learned[k]) Add(term, k, Options.LearnedWeight);
        return vocabulary;
    }

    private Evidence Collect(IReadOnlyList<Post> posts, Vocabulary vocabulary)
    {
        var evidence = new Evidence();
        foreach (var post in posts)
        {
            var postLabels = LabelsOf(post);
            if (postLabels.Count == 0) continue;

            var said = MatchTerms(PostEvidence(post), vocabulary);
            var byAuthor = MatchTerms(AuthorEvidence(post), vocabulary);
            foreach (var term in said.Concat(byAuthor).Distinct(StringComparer.OrdinalIgnoreCase))
                evidence.TermPosts[term] = evidence.TermPosts.GetValueOrDefault(term) + 1;

            // A post about one thing says more about that thing than a post with eight hashtags
            // stapled to it, so tag-stuffed posts count proportionally less for each subject.
            var share = 1.0 / postLabels.Count;

            foreach (var label in postLabels)
            {
                var authoritative = SpeaksForItself(post, label);
                var terms = authoritative
                    ? said.Concat(byAuthor).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : said;
                if (authoritative) evidence.Authoritative[label.Key] = evidence.Authoritative.GetValueOrDefault(label.Key) + 1;

                if (terms.Count > 0)
                {
                    evidence.Posts[label.Key] = evidence.Posts.GetValueOrDefault(label.Key) + 1;
                    var bag = evidence.Bags.TryGetValue(label.Key, out var b)
                        ? b : evidence.Bags[label.Key] = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    var weight = authoritative ? Options.AuthorWeight : share;
                    foreach (var term in terms) bag[term] = bag.GetValueOrDefault(term) + weight;
                }

                var neighbours = evidence.Cooccurrence.TryGetValue(label.Key, out var n)
                    ? n : evidence.Cooccurrence[label.Key] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var other in postLabels)
                    if (!ReferenceEquals(other, label))
                        neighbours[other.Key] = neighbours.GetValueOrDefault(other.Key) + 1;
            }
        }
        return evidence;
    }

    private void Decide(Evidence evidence, Vocabulary vocabulary, List<LabelKind> kinds)
    {
        // Words that fire everywhere say little; rare ones are the telling ones.
        double Weight(string term) =>
            vocabulary.Trust.GetValueOrDefault(term, 1.0) / Math.Log(2 + evidence.TermPosts.GetValueOrDefault(term));

        foreach (var label in Labels)
        {
            if (!evidence.Bags.TryGetValue(label.Key, out var bag)) continue;
            if ((double)evidence.Posts.GetValueOrDefault(label.Key) / label.PostCount < Options.MinConfidence) continue;

            var scores = new double[kinds.Count];
            var distinct = new int[kinds.Count];
            for (int k = 0; k < kinds.Count; k++)
            {
                foreach (var (term, count) in bag)
                    if (vocabulary.OfKind[k].Contains(term))
                    {
                        scores[k] += count * Weight(term);
                        distinct[k]++;
                    }
                scores[k] *= kinds[k].Weight;
            }

            var best = 0;
            for (int k = 1; k < kinds.Count; k++) if (scores[k] > scores[best]) best = k;
            if (scores[best] <= 0) continue;

            // One ambiguous word is not a verdict — unless the subject's own account said it.
            if (distinct[best] < Options.MinTerms && evidence.Authoritative.GetValueOrDefault(label.Key) == 0) continue;

            var runnerUp = scores.Where((_, k) => k != best).DefaultIfEmpty(0).Max();
            // A close call is not a decision — leave it to the company the label keeps.
            if (runnerUp > 0 && scores[best] / runnerUp < Options.MinMargin) continue;

            label.Kind = kinds[best].Name;
            label.Confidence = scores[best] / scores.Sum();
            label.Evidence = bag.Where(kv => vocabulary.OfKind[best].Contains(kv.Key))
                .OrderByDescending(kv => kv.Value * Weight(kv.Key)).Take(4).Select(kv => kv.Key).ToList();
        }
    }

    /// <summary>
    /// Reads the posts of the subjects that were confidently classified and works out which other
    /// words belong to each kind: a word is learned when it leans strongly towards one kind rather
    /// than being spread across the corpus. Nothing here knows any title — the vocabulary grows out
    /// of how the logged accounts actually write.
    /// </summary>
    private List<List<string>> Learn(IReadOnlyList<Post> posts, List<LabelKind> kinds, Vocabulary seeds)
    {
        var perKind = new Dictionary<string, double>[kinds.Count];
        for (int k = 0; k < kinds.Count; k++) perKind[k] = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var kindIndex = kinds.Select((k, i) => (k.Name, i))
            .ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);

        foreach (var post in posts)
        {
            var postLabels = LabelsOf(post);
            var classified = postLabels.Where(l => l.Kind != Options.UnknownName && !l.Inherited).ToList();
            if (classified.Count == 0) continue;

            var words = Word().Matches(PostEvidence(post)).Select(m => m.Value)
                .Where(w => w.Length >= 3 && !w.All(char.IsDigit))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var share = 1.0 / postLabels.Count;

            foreach (var label in classified)
            {
                var k = kindIndex[label.Kind];
                foreach (var word in words)
                {
                    perKind[k][word] = perKind[k].GetValueOrDefault(word) + share;
                    totals[word] = totals.GetValueOrDefault(word) + share;
                }
            }
        }

        // Words that are part of a subject's own name are not vocabulary — learning "genshin" as a
        // sign of video games would just be the detector agreeing with itself. Only words that
        // describe things may be learned.
        var nameWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in Labels)
        {
            nameWords.Add(label.Key);
            foreach (var variant in label.Variants.Append(label.Display))
                foreach (Match m in Word().Matches(SplitWords(variant.TrimStart('#'))))
                    nameWords.Add(m.Value);
        }
        // Nor are the people posting: "this voice actor keeps showing up" is not a description
        // of what a subject is, it is just who talks about it.
        foreach (var post in posts)
        {
            nameWords.Add(NormalizeKey(post.Handle));
            if (post.RepostedBy is not null) nameWords.Add(NormalizeKey(post.RepostedBy));
            if (post.QuotedHandle is not null) nameWords.Add(NormalizeKey(post.QuotedHandle));
        }

        var grandTotal = totals.Values.Sum();
        var learned = new List<List<string>>();
        for (int k = 0; k < kinds.Count; k++)
        {
            var baseRate = grandTotal > 0 ? perKind[k].Values.Sum() / grandTotal : 0;
            var floor = Math.Max(Options.LearnedMinShare, baseRate * Options.LearnedLift);
            learned.Add(perKind[k]
                .Where(kv => kv.Value >= Options.LearnedMinCount
                             && kv.Value / totals[kv.Key] >= floor
                             && !seeds.Trust.ContainsKey(kv.Key)
                             && !nameWords.Contains(kv.Key))
                .OrderByDescending(kv => kv.Value)
                .Take(Options.LearnedTermsPerKind)
                .Select(kv => kv.Key)
                .ToList());
        }
        return learned;
    }

    /// <summary>Unclassified labels take the kind of the company they keep.</summary>
    private void Propagate(Dictionary<string, Dictionary<string, int>> cooccurrence)
    {
        var known = Labels.Where(l => l.Kind != Options.UnknownName)
            .ToDictionary(l => l.Key, l => l.Kind, StringComparer.OrdinalIgnoreCase);

        foreach (var label in Labels)
        {
            if (label.Kind != Options.UnknownName) continue;
            if (!cooccurrence.TryGetValue(label.Key, out var neighbours)) continue;

            var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var total = 0;
            foreach (var (key, weight) in neighbours)
                if (known.TryGetValue(key, out var kind))
                {
                    votes[kind] = votes.GetValueOrDefault(kind) + weight;
                    total += weight;
                }
            if (total == 0) continue;

            var winner = votes.OrderByDescending(kv => kv.Value).First();
            if ((double)winner.Value / total < Options.PropagateShare) continue;
            label.Kind = winner.Key;
            label.Confidence = (double)winner.Value / total;
            label.Inherited = true;
        }
    }

    /// <summary>What the post itself says — deliberately excludes who wrote it, so that (say) a
    /// voice actor's job title in their display name does not colour every subject they mention.</summary>
    private static string PostEvidence(Post post)
    {
        var sb = new StringBuilder();
        sb.Append(post.Text).Append(' ').Append(post.QuotedText);
        foreach (var tag in post.Hashtags) sb.Append(' ').Append(tag);
        // Link targets are plumbing, not language — without this every post would "mention" twitter.
        return LinkTarget().Replace(sb.ToString().ToLowerInvariant(), " ");
    }

    private static string AuthorEvidence(Post post) => (post.DisplayName + " " + post.Handle).ToLowerInvariant();

    /// <summary>True when the post comes from the subject's own account (@GenshinImpact posting
    /// about #GenshinImpact), which makes it authoritative about what the subject is.</summary>
    private static bool SpeaksForItself(Post post, LabelInfo label)
    {
        if (label.Key.Length < 5) return false;
        return NormalizeKey(post.Handle).Contains(label.Key, StringComparison.OrdinalIgnoreCase)
               || NormalizeKey(post.DisplayName).Contains(label.Key, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> MatchTerms(string evidence, Vocabulary vocabulary)
    {
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Word().Matches(evidence).Select(m => m.Value))
            if (vocabulary.Tokens.ContainsKey(token)) matched.Add(token);
        foreach (var (term, _) in vocabulary.Phrases)
            if (evidence.Contains(term, StringComparison.OrdinalIgnoreCase)) matched.Add(term);
        return matched.ToList();
    }

    private List<LabelInfo> LabelsOf(Post post)
    {
        var result = new List<LabelInfo>();
        foreach (var key in post.Hashtags.Select(NormalizeKey).Distinct(StringComparer.OrdinalIgnoreCase))
            if (ByKey.TryGetValue(key, out var label))
                result.Add(label);
        return result;
    }

    // ---- 3. stamp the findings onto the posts ----------------------------------------------

    private void Annotate(IReadOnlyList<Post> posts)
    {
        foreach (var post in posts)
        {
            var labels = LabelsOf(post);
            post.Labels = labels.Select(l => l.Display).ToList();
            post.Kinds = labels.Where(l => l.Kind != Options.UnknownName)
                .Select(l => l.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Resolves a user-typed label to its normalized key — display name or raw tag both work.</summary>
    public string? Resolve(string text)
    {
        var key = NormalizeKey(text);
        if (ByKey.ContainsKey(key)) return key;
        return Labels.FirstOrDefault(l => l.Display.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))?.Key;
    }
}
