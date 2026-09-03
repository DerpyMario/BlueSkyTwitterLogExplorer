-- ============================================================================
-- filters.lua — filtering rules for the BlueSky/Twitter DiscordLog explorer
--
-- This file is executed by the C# app (MoonSharp). Everything about *what*
-- gets filtered lives here; the C# side only supplies the parsed posts.
--
-- A post table passed to the functions below has these fields:
--   post.platform      "twitter" | "bluesky"
--   post.handle        author handle without @ (e.g. "vamichaelalaws", "ghaspey.bsky.social")
--   post.display_name  author display name
--   post.text          post text (markdown stripped of URLs when hashtags were extracted)
--   post.url           canonical post URL
--   post.timestamp     ISO-8601 string     post.unix   unix seconds (number)
--   post.date          "YYYY-MM-DD"        post.time   "HH:MM:SS" (UTC)
--   post.hashtags      array of lower-cased hashtags (no #)
--   post.reposted_by   handle that retweeted/reposted it, or nil
--   post.is_repost     boolean
--   post.quoted_handle / post.quoted_text   quoted post, or nil
--   post.channel / post.source_file         where the post came from
-- ============================================================================

options = {
    -- strict = true  → a hashtag counts for a category only when it matches a
    --                  category tag EXACTLY (case-insensitive): #anime matches
    --                  "anime", #animeart does NOT.
    -- strict = false → prefix matching: #animeart would match "anime".
    strict = true,

    -- When true, posts that end up in no category are dropped from every
    -- command unless you explicitly ask for them (--category Uncategorized).
    require_category = false,

    uncategorized_name = "Uncategorized",
}

-- Default date/time window, applied automatically whenever --from/--to/--between
-- are not given on the command line. Times are UTC. Leave values nil (or the
-- table empty) to explore the full range of the exports.
date = {
    from = nil,             -- e.g. "2026-06-20"
    to = nil,               -- e.g. "2026-07-31" (inclusive, whole day)
    time_from = nil,        -- e.g. "06:00"  → only posts between these
    time_to = nil,          -- e.g. "12:00"    times of day (UTC)
}

-- ============================================================================
-- Categories. Each category matches a post when ANY of these applies:
--   * one of the post's hashtags matches one of `tags` (per options.strict)
--   * the author handle is listed in `users`
--   * the optional `match = function(post)` returns true
-- A post can land in several categories; posts matching none go to
-- "Uncategorized".
-- ============================================================================
categories = {
    VoiceActing = {
        tags = { "voiceactor", "voiceacting", "voiceover", "va", "dub", "dubbing",
                 "casting", "voicedirector", "audiodrama" },
    },

    AnimeManga = {
        tags = { "anime", "manga", "onepiece", "witchhatatelier", "aphmau",
                 "daemonsoftheshadowrealm", "saturdaemons", "yominotsugai",
                 "黄泉のツガイ", "ヨミツガ", "彼女お借りします", "rentagirlfriend",
                 "goodbyelara", "さよならララ", "さよララファンアート部",
                 "worldoffiction", "knightsofguinevere", "tamonsbside",
                 "indieanimation", "animation" },
    },

    Gaming = {
        tags = { "genshinimpact", "zenlesszonezero", "zzzero", "ゼンゼロ",
                 "honkaistarrail", "marvelrivals", "fireemblem", "gaming",
                 "videogames", "nintendo", "playstation" },
    },

    GameDev = {
        tags = { "gamedev", "indiegame", "indiedev", "gamedevelopment",
                 "indiegames", "gamejam", "screenshotsaturday" },
    },

    ArtIllustration = {
        tags = { "art", "fanart", "illustration", "drawing", "sketch",
                 "digitalart", "artistsontwitter", "commission", "commissions" },
    },

    OtomeAudio = {
        tags = { "otome", "otomecd", "audiocd", "asmr", "audioroleplay",
                 "romance", "dramacd" },
    },

    VTuber = {
        tags = { "vtuber", "envtuber", "vtuberen", "nijisanji", "hololive",
                 "vtubers" },
        -- Handles can force a category regardless of hashtags:
        users = { "Tyrant_Vanta" },
    },

    Events = {
        tags = { "ax2026", "animeexpo", "animeexpo2026", "comiket", "twitchcon" },
        -- Custom rules run as real Lua — anything you can compute from the
        -- post is fair game:
        match = function(post)
            local text = string.lower(post.text or "")
            return string.find(text, "anime expo", 1, true) ~= nil
                or string.find(text, "artist alley", 1, true) ~= nil
        end,
    },
}


-- ============================================================================
-- Labels: automatic subject detection.
--
-- The explorer mines subject *names* (games, shows, events, …) straight out of
-- the hashtags in your logs — no list of titles is written down anywhere, so new
-- games and shows are picked up on their own as they appear in the exports.
--
-- What IS configured here is the plain vocabulary that tends to surround each
-- KIND of subject. A label is filed under the kind whose vocabulary shows up
-- most often in the posts that use it: something discussed with "patch notes"
-- and "wishlist" is a video game, something discussed with "episode" and
-- "simulcast" is anime. Labels with no vocabulary of their own inherit the kind
-- of the labels they keep appearing next to.
--
-- Add, rename or delete kinds freely — they are just names with word lists. An optional
-- `weight` scales how much a kind's vocabulary counts (see the notes inline below).
-- ============================================================================
labels = {
    min_posts = 3,           -- how often a hashtag must appear to count as a subject
    min_confidence = 0.05,   -- share of a label's posts that must show a kind's vocabulary
    min_margin = 1.3,        -- how far ahead of the runner-up the winning kind must be
    author_weight = 6.0,     -- extra trust in posts from the subject's own account
    min_terms = 2,           -- how many *different* words must back a decision
    learned_terms_per_kind = 40, -- vocabulary the explorer teaches itself from the corpus (0 = off)
    learned_weight = 0.5,    -- how much a learned word counts next to a hand-written one
    propagate = true,        -- unclassified labels inherit from the labels they co-occur with
    propagate_share = 0.4,   -- ... when that much of the co-occurrence agrees on one kind
    exclude_signal_words = true, -- a hashtag that IS vocabulary (#anime) is not a subject name
    unknown_name = "Unclassified",

    kinds = {
        -- Every entry below is ordinary vocabulary, never the name of a work. Words that
        -- describe a medium ("gamedev", "fanart") also keep themselves out of the subject
        -- list, since a hashtag that IS vocabulary is a topic, not a title.
        --
        -- Words that genuinely belong to several kinds ("trailer", "chapter", "season") are
        -- listed under each of them on purpose: shared words then cancel out and the decision
        -- rests on the vocabulary that actually tells the kinds apart.
        ["Video game"] = {
            -- telling
            "gameplay", "playthrough", "speedrun", "patchnotes", "hotfix", "dlc",
            "playtest", "earlyaccess", "wishlist", "steam", "gacha", "pity",
            "roguelike", "deckbuilder", "respawn", "questline", "sidequest",
            "gaming", "gamedev", "gamedevelopment", "indiegame", "indiegames",
            "indiedev", "gamejam", "screenshotsaturday", "videogame", "videogames",
            "playable", "speedrunning", "playstation", "nintendo", "xbox",
            "out now on steam", "wishlist now", "ゲーム", "実装", "アップデート", "プレイ",
            -- shared with other kinds
            "trailer", "chapter", "season", "update", "version", "banner", "boss",
            "quest", "level", "character", "release",
        },
        ["Anime"] = {
            "anime", "simulcast", "subbed", "dubbed", "cour", "ova", "ona", "isekai",
            "shonen", "shounen", "shoujo", "seinen", "crunchyroll", "animated series",
            "アニメ", "アニメ化", "放送", "最新話", "第話",
            "episode", "season", "arc", "adaptation", "opening", "ending", "trailer",
            "chapter", "streaming", "animation",
        },
        ["Manga & comics"] = {
            "manga", "webtoon", "oneshot", "graphicnovel", "serialization",
            "単行本", "連載", "漫画", "comic", "comics",
            "chapter", "volume", "arc",
        },
        ["Film & TV"] = {
            "boxoffice", "cinema", "screening", "documentary", "in theaters",
            "live action", "film festival", "film", "movie",
            "trailer", "season", "episode", "director", "sequel", "streaming",
        },
        ["Audio & voice"] = {
            -- weight < 1: a voice credit is an occasion around a work, not the work itself
            weight = 0.6,
            "voiceactor", "voiceacting", "voiceover", "voiceoverartist", "voicedirector",
            "castingcall", "audiodrama", "dramacd", "asmr", "binaural", "audioroleplay",
            "otome", "otomecd", "audiocd", "soundtrack", "ost", "album", "remix",
            "composer", "楽曲", "アルバム", "主題歌", "声優",
            "song", "cover", "release",
        },
        ["Event"] = {
            -- convention talk surrounds every subject, so it needs much stronger evidence
            weight = 0.35,
            "convention", "expo", "artistalley", "booth", "badge", "autograph",
            "exhibitor", "tabling", "meetandgreet", "see you there", "at table",
            "guest", "panel", "lineup", "tickets",
        },
        ["Art & illustration"] = {
            -- likewise: fanart is drawn *about* things, so it rarely defines what they are
            weight = 0.4,
            "fanart", "illustration", "lineart", "digitalart", "artbook", "procreate",
            "chibi", "artistsontwitter", "イラスト", "描いた",
            "art", "commission", "commissions", "drawing", "sketch",
        },
    },

    -- Optional last word on any detected label. Gets { name, key, kind, confidence,
    -- inherited, posts, users, evidence, related, variants } and returns a kind name
    -- (or nil to keep what the evidence decided).
    detect = function(label)
        -- e.g. keep thinly-evidenced guesses out of the way:
        -- if label.inherited and label.posts < 5 then return "Unclassified" end
        return nil
    end,
}

-- ============================================================================
-- Global exclusion: return true to drop a post entirely, before any other
-- filter runs. Handy for muting spam or accounts you don't care about.
-- ============================================================================
local muted_users = {
    -- ["some.spammer.bsky.social"] = true,
}

function exclude(post)
    if muted_users[string.lower(post.handle)] then return true end
    return false
end

-- ============================================================================
-- Fallback categorization: called only for posts no category matched.
-- Return a category name (string), a table of names, or nil to leave the
-- post uncategorized.
-- ============================================================================
function categorize(post)
    -- Example: bucket link-only posts.
    -- if post.text == "" then return "MediaOnly" end
    return nil
end
