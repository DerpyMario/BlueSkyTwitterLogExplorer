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
