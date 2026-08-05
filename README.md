# BlueSky/Twitter Log Explorer

A file, list and post explorer for **Bluesky** and **Twitter/X** activity that was mirrored
into Discord (TweetShift / SkyCord bots) and exported with **DiscordLog** or
**DiscordChatExporter**. Written in **C#** (.NET 8) with all filtering rules scripted in
**Lua** (embedded via MoonSharp — no separate Lua install needed).

It recovers the original social-media posts out of the Discord message dumps and lets you
explore them per user, per hashtag category and per date/time window:

* **Every user indexed** — each Bluesky/Twitter author found in the logs, with post and
  repost counts, first/last seen dates, top hashtags and categories.
* **Strict hashtag filtering by category** — categories are defined in
  [`scripts/filters.lua`](scripts/filters.lua). In strict mode `#anime` matches the
  `anime` tag exactly and `#animeexpo` does not; loose mode allows prefix matching.
  Categories can also match by author handle or by an arbitrary Lua function.
* **Automatic date & time filtering** — every post gets its real post timestamp (tweet
  time, Bluesky `<t:…>` time; the Discord message time is a fallback), file date ranges
  are read from export metadata/filenames, overlapping exports are de-duplicated, and a
  default date/time window in the Lua script is applied automatically to every command.

## Data formats understood

| Format | What is parsed |
|---|---|
| DiscordLog JSON (`{serverName, channelName, fromDate, toDate, messages}`) | Tweets from TweetShift embeds **and** Bluesky posts from Components-V2 blocks (posts, reposts, quotes) |
| DiscordChatExporter JSON (`{guild, channel, dateRange, messages}`) | Tweets from embeds (DiscordChatExporter does not capture Bluesky component content) |
| CSV / TXT / MD / HTML exports | Listed by the `files` command with their date ranges (the JSON exports carry the same messages, so these are not parsed) |

Retweets are detected (TweetShift keeps the retweeter in the status URL and the original
author in the embed), Bluesky reposts/quotes are parsed from the component markup, x.com
URLs are canonicalized, and posts appearing in several overlapping exports are counted once.

## Getting started

```bash
# 1. Extract your DiscordLog .7z exports somewhere, e.g. ./data
#    (the repo ships tiny synthetic files in ./sample-data to try it out)

# 2. Explore
dotnet run --project src/LogExplorer -- files --data data
dotnet run --project src/LogExplorer -- users --data data --top 25 --detail
dotnet run --project src/LogExplorer -- posts --data data --category VoiceActing --from 2026-07-01 --to 2026-07-15
dotnet run --project src/LogExplorer -- tags  --data data --top 50
dotnet run --project src/LogExplorer -- categories --data data
dotnet run --project src/LogExplorer -- export --data data --tag gamedev --format csv --out gamedev.csv
dotnet run --project src/LogExplorer -- interactive --data data
```

`--data` defaults to `./data` (or `$LOGEXPLORER_DATA`); the directory is scanned recursively,
so you can extract each `.7z` archive into its own subfolder.

## Commands

| Command | Purpose |
|---|---|
| `files` | Every export file with format, platform, date range, message and parsed-post counts |
| `users` | Every Bluesky/Twitter user seen in the logs (`--sort posts\|recent\|handle`, `--top`, `--find`, `--detail`) |
| `posts` | Posts matching the filters (`--full`, `--sort old\|new`) |
| `tags` | Hashtags with counts and the categories they map to |
| `categories` | The Lua categories with post counts, user counts and top users |
| `export` | Write filtered posts to `json`, `csv` or `md` (`--out`, `--format`) |
| `interactive` | REPL accepting all of the above |

### Filters (work on every command)

```
--platform twitter|bluesky     --user handle1,handle2      (author or reposter)
--tag anime,gamedev            --category VoiceActing      (from filters.lua)
--from 2026-07-01              --to 2026-07-15T12:00       (UTC; bare --to date = whole day)
--last 7d|12h|30m              (measured back from the newest post in the data)
--between 06:00-12:00          (time-of-day window, may wrap midnight)
--contains "text"              --where "post.is_repost and #post.hashtags > 1"   (Lua!)
--no-reposts / --only-reposts  --limit N
```

## The Lua side (`scripts/filters.lua`)

All filtering *rules* live in Lua; the C# side only parses and applies them:

```lua
options = {
    strict = true,             -- exact hashtag matching per category
    require_category = false,  -- drop uncategorized posts everywhere
}

date = {                       -- applied automatically when no CLI flags given
    from = "2026-07-01", to = "2026-07-31",
    time_from = "06:00", time_to = "12:00",   -- UTC time-of-day window
}

categories = {
    VoiceActing = { tags = { "voiceactor", "voiceacting", "casting" } },
    VTuber = {
        tags = { "vtuber", "nijisanji" },
        users = { "Tyrant_Vanta" },           -- handles force the category
        match = function(post)                -- arbitrary Lua per post
            return string.find(string.lower(post.text), "debut stream", 1, true) ~= nil
        end,
    },
}

function exclude(post)         -- drop posts before any other filter runs
    return post.handle == "some.spammer.bsky.social"
end

function categorize(post)      -- fallback for posts no category matched
    return nil
end
```

Ad-hoc Lua predicates also work straight from the CLI:

```bash
dotnet run --project src/LogExplorer -- posts --data data \
    --where "post.platform == 'bluesky' and post.quoted_handle ~= nil"
```

## Building & tests

```bash
dotnet build
dotnet test          # parser + Lua filter engine tests
```

Project layout:

```
src/LogExplorer/         C# console app (parsers, dedupe, Lua bridge, CLI)
scripts/filters.lua      the Lua rule set (copied next to the binary on build)
sample-data/             tiny synthetic exports for a quick demo
tests/LogExplorer.Tests/ xunit tests
```
