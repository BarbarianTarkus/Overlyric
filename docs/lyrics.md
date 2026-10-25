# Lyrics: finding and choosing the right record

LRCLIB usually holds several records for the same song, and they do not all carry timings. This page
describes how Overlyric picks one, how to override that pick, and what the app does with instrumental
tracks and transient LRCLIB failures.

## Matching the right record

lrclib usually holds many records for the same song — studio, live, remastered, assorted
compilations — and they differ in whether they carry timings at all. Finding the one that matches the
performance actually playing takes some cleaning, some ranking, and — as often as not — a second
question.

**Release suffixes are stripped before querying.** TIDAL reports
`A Song For All Seasons (2019 Remaster)`, and lrclib holds records whose names carry that exact
suffix — but with *plain* lyrics only. Asking for the suffixed name returns precisely that family, so
the app concluded the track had no timings and displayed "Lyrics not synced". Asking for
`A Song For All Seasons` instead returns twenty candidates including a synced record with an identical
duration and album. The suffix was not merely untidy; it selected the wrong record.

Qualifiers that identify a genuinely different performance — `(Live)`, `(Acoustic)` — are deliberately
**not** stripped, so a live take never silently resolves to the studio cut.

**Movement listings are cut down to the work.** Some players report a multi-part piece as one
enormous title: TIDAL sends `Song of Scheherazade / I. Fanfare - II. / The Betrayal / III. The Sultan /
…` as the track name, and no lrclib record carries anything like it, so searching the whole string
finds nothing. Cutting at the first spaced slash — but only when what follows actually looks like a
movement list, so a title such as `Love / Hate` survives — reduces it to `Song of Scheherazade`, which
returns twenty candidates including six with timings. The same cleaning is applied to the on-screen
header, which would otherwise stretch across the entire display.

**Candidates are ranked, and duration dominates.** A synced lyric sheet for a different performance is
worse than untimed lyrics for the right one, because it would drift out of step with the music. So a
candidate that is minutes out of length can never win on the strength of having timings; among
candidates whose duration agrees, timings, an exact title and a matching album break the tie.

**When the pick carries no timings, the first record in rank order that has them and runs for the same
length wins.** Untimed lyrics for the right performance beat a synced sheet for the wrong one, but they
are not the best answer available — timings for *that* performance are. So when the ranked pick is not
synced, the app takes the first record in rank order that carries real timings **and** runs for the same
length as the track being played. "The same length" is `|candidate - track| <= max(2 s, 0.5 % of the
track)` — the band the ranking already treats as an agreeing duration, and exactly lrclib's own window
at that size: `/api/get` matches a duration within two seconds, measured directly (asking it for 251 s
returns the 249.051429 s record, asking for 252 s returns 404). Only a piece longer than 400 s widens
it, because that is where the proportional term overtakes the two seconds. Exact equality is not a
thing that exists in this data: the player reported 246.4 s for a sheet that runs 244.744127 s, 1.656 s
apart, so a test for equality would never have fired. A synced sheet for a different performance fails
on length alone and is never chosen by this rule — the same signal the ranking already trusts. An
instrumental verdict is not overridden on the strength of timings either: `Metallica – Orion` still
comes back an instrumental with no lines. When no synced record matches the length, the plain result is
kept, and when the player publishes no duration the rule cannot apply at all, because there is nothing
to compare against.

**That answer lives behind a second request, because lrclib caps `/api/search` at twenty records.**
Measured directly: `track_name=Winning` returns 20 records, `track_name=For+Nobody` returns 20, and
`q=Winning` returns 20. The cap can hide the very record the rule is looking for. For the track playing
while this was written — Gentle Giant, *For Nobody*, from *The Missing Piece*, 246.4 s — the
track-and-artist search returned twenty records and **not one of them carried timings**, while the same
query plus `album_name=The Missing Piece` is the only one that contains the synced record 16359843,
"09 - For nobody", 244.744127 s, twenty lines, first line `Running away, I'll leave you my address,` at
0:46.88. So when — and only when — the first search leaves the track unsynced, the search is repeated
with the album and the two lists are ranked together; the app now resolves that track to the synced
record. A track the first search already answers well still costs exactly one `/api/search`, because the
album-scoped question is only asked when the first one left the track untimed. The merged list is also
what the lyric selector shows: for the live case it held 35 records, with id 16359843 appearing exactly
once.

## Choosing a version by hand

The automatic pick is a guess, however good, so **Choose lyrics** opens a window listing every record
lrclib holds for the track, best first. Each row carries what a menu could not: the record's own
title and artist, its album, its length, how far that length is from the track playing, its lrclib
id, and one badge for the promise it makes — **Word-synced** (the karaoke sweep follows real word
timings), **Synced** (line timings only), **Plain** (words without timings), **Instrumental**, or
**No lyrics**. The row in use is marked **In use**; the one chosen by hand is marked **Pinned**.
**Automatic (best match)** at the top returns to the ranked pick.

Picking a row applies it immediately, because the overlay is visible behind the selector: choosing a
version and hearing the effect are one action, not two. The choice is remembered across plays and
restarts in `%LOCALAPPDATA%\Overlyric\versions.json`.

Automatic is a guess, and sometimes a close one: for Gentle Giant's *Winning* (256 s) the automatic pick
is the synced record 36334456 (257 s), while the record a listener had pinned by hand, 14916772
(257.52 s), sits just below it in rank order and is equally inside the duration window.

The candidate list — the merged, ranked list described above — is fetched up front when the resolved
lyrics are missing or untimed — exactly where a version change is likely — and on demand when the
selector is opened for a track whose automatic answer was good. A track that already has good synced
lyrics costs no extra request until someone asks to change it.

## The complete lyric sheet

**Full lyrics** opens the whole sheet in its own window: every line, its timestamp, the record it
came from, and the same badge the selector uses. The line being sung is highlighted and scrolled into
view when the window opens. With plain lyrics the timestamp column is absent rather than filled with
zeroes, because zeroes would claim a timing the sheet does not have.

The window is read-only today, and deliberately so: the next stage is editing the sheet and uploading
it to LRCLIB. Both already have a place to live because the window draws from `LyricSheet` — the
parsed lines, the track details and the record identity in one document — instead of from a formatted
string. While it is open it follows the track: a new lookup or a different chosen version replaces the
document, and a new record scrolls back to the active line.

## Notes on LRCLIB

The client uses `/api/get` with artist, title, album and duration for an exact match, falling back to
`/api/search` and picking the best candidate by duration, preferring entries that carry synced lyrics
and upgrading an untimed pick to the first timed record of the same length (see [Matching the right
record](#matching-the-right-record)); when that first search leaves the track unsynced it is repeated
with the album, because lrclib caps a search at twenty records. Every request sends a descriptive
`User-Agent`, as LRCLIB asks. Network failures degrade to "no lyrics"; they never disturb playback.

**A transient outage must never become a permanent verdict.** LRCLIB throttles bursts and regularly
answers `503 Server is busy`, which was observed repeatedly during development. Treating that as "this
track has no lyrics" and negative-caching it would hide a track's lyrics forever after one bad
moment. So the client distinguishes three outcomes:

| Response | Meaning | Cached? |
| --- | --- | --- |
| `200` with lyrics | Found | Yes, as lyrics |
| `404`, or a record with no lyrics at all | Definitively absent | Yes, as a tombstone |
| `429`, `5xx`, timeout, network error | Unknown — ask again later | **No** |

Throttled and failed requests are retried once before giving up, and only a definitive answer is
remembered. This is covered by `probe\ResilienceTests`, which points the client at a local stub server
returning `503`, `429`, `404` and lyric-less `200` responses and asserts both the classification and
the caching decision.

Unsynced lyrics are shown only after the search for a synced sheet of the same length has come up empty:
then single-line mode says `Lyrics not synced` rather than guessing, and multi-line mode scrolls the
text proportionally to track position.

## Instrumental tracks

"There is nothing to sing here" and "we could not find the words" are different answers, and a third
case matters just as much: **lrclib records whose entire lyric body is placeholder text.** Contributors
routinely write `Instrumental` or `[Instrumental]` instead of setting the record's `instrumental` flag,
and parsed naively that becomes a real lyric line — so the overlay ends up displaying the word
"Instrumental" in time with the music. The flag cannot be relied on, so the body itself is inspected.

`InstrumentalDetector` reduces each line to comparable words — dropping `[mm:ss.xx]` timestamps and
`[ar:…]` metadata tags, discarding brackets, musical symbols and punctuation — then checks whether the
**whole** body consists of phrases that mean "no words here":

| Recognises | Examples |
| --- | --- |
| The word itself | `Instrumental`, `[Instrumental]`, `Instrumental Break`, `Instrumental Version` |
| Explicit statements | `No lyrics`, `Without vocals`, `Lyrics not available` |
| Nothing to sing | `♪`, `Silence`, `Tacet`, `Spoken` |
| Instrumental forms and section names | `[Intro]`, `[Solo]`, `[Outro]`, `Overture`, `[Chorus]` |

The safety property is that a verdict requires *every* line to be a placeholder. A real song with words
plus an `[Instrumental Break]` or `[Chorus]` marker in the middle is unaffected, because its other
lines are not placeholders — that case is pinned down by a test.

Detection works in three layers, in order:

1. lrclib's `instrumental` flag (authoritative — confirmed against real data; `Metallica – Orion`
   returns `"instrumental":true` with both lyric bodies `null`).
2. The lyric body itself, as described above, for records where the flag was left unset.
3. The title or album, **only** when lrclib has no record at all. A title like
   `Song (Instrumental)` or an album called `Instrumentals` gives a better answer than
   "no lyrics found". `Karaoke` is deliberately *not* treated as instrumental: a karaoke track has no
   lead vocal but the words are exactly what you want to see.

Both kinds of empty answer are cached, so an instrumental is not re-queried on every replay, and they
can be hidden independently — some people want the overlay to stay put and simply say "Instrumental"
on a wordless track rather than vanishing.
