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

**Album suffixes are cleaned the same way, and the album is retried before it is dropped.** TIDAL
reports `The Lamb Lies Down on Broadway (2007 Stereo Mix)`; when lrclib files that edition under the
plain album, asking for the suffixed name returns nothing, and the album-scoped search — the one
search where an album is a filter — returns nothing either. So the exact lookup asks for the reported
name first, because that is the record of the edition actually playing when lrclib holds it; then for
the cleaned name; and only then does it drop the album entirely. The album-scoped search always asks
the cleaned name, so a release qualifier can no longer hide the record the twenty-record cap left out.
The cache's matching version was bumped with the change, so a track that was already missed is
resolved again rather than served from its old tombstone.

Qualifiers that identify a genuinely different performance — `(Live)`, `(Acoustic)` — are deliberately
**not** stripped, so a live take never silently resolves to the studio cut.

**A duplicated artist prefix is removed.** Some players, YouTube among them, publish
`King Crimson - Two Hands` as the *track title* while separately naming `King Crimson` as the artist.
lrclib holds `Two Hands`, and asking it for `King Crimson - Two Hands` returns **no records at all** —
so the track used to settle as "No lyrics found" and cache that verdict. When the part before the dash
matches the reported artist, the prefix is dropped before any request; the comparison is exact (after
case and punctuation folding), so a title that legitimately begins with somebody else's name is left
alone. A prefix repeated twice is removed too, and the release-suffix and movement passes run again on
what is left, so `King Crimson - Two Hands [50th Anniversary | …]` becomes `Two Hands`. The cache's
matching version was bumped with the change, so a track that was already wrong is resolved again rather
than served from its old tombstone.

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
0:46.88. So when — and only when — the first search leaves the track unsynced **and comes back
full**, the search is repeated with the album and the two lists are ranked together; the app now
resolves that track to the synced record. A track the first search already answers well still costs
exactly one `/api/search`, because the album-scoped question is only asked when the first one left
the track untimed.

The "comes back full" part is not caution, it is arithmetic. `album_name` narrows a search rather
than widening it — `album_name=ZZZnotarealalbum` returns nothing at all, measured — so an
album-scoped answer is always a subset of the unfiltered match set, and a subset can only be missing
from a page that was truncated. A search that returned nineteen records was complete. A track lrclib
does not hold at all therefore costs three requests rather than four, plus the album-cleaning retry
when the reported name carries a release suffix. The merged list is also what the lyric selector
shows: for the live case it held 35 records, with id 16359843 appearing exactly once.

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
restarts in `%LOCALAPPDATA%\Overlyric\selections.json` (the older `versions.json` is migrated on first
load), together with the third kind of choice: a local file.

Automatic is a guess, and sometimes a close one: for Gentle Giant's *Winning* (256 s) the automatic pick
is the synced record 36334456 (257 s), while the record a listener had pinned by hand, 14916772
(257.52 s), sits just below it in rank order and is equally inside the duration window.

The candidate list — the merged, ranked list described above — is fetched up front when the resolved
lyrics are missing or untimed — exactly where a version change is likely — and on demand when the
selector is opened for a track whose automatic answer was good. A track that already has good synced
lyrics costs no extra request until someone asks to change it.

It is also written to the cache beside the answer, because it is the most expensive thing a
resolution downloads: one search answer is twenty records with their full bodies. A track resolved by
searching in one session therefore opens its selector at no cost in the next. **Look again** is the
only thing that throws the list away and asks lrclib afresh.

## Your own lyric file

LRCLIB is a catalogue, not the last word. **Import file…** in the selector's footer opens a file
picker for `.lrc` or `.txt`, and the file you choose is previewed before anything is stored: its name,
how many timed lines it holds, the first and last timestamps, whether its length matches the track,
and a warning when it has no timestamps at all. **Import** stores it under
`%LOCALAPPDATA%\Overlyric\lyrics` - a `.lrc` plus a small metadata `.json` - and makes it the
explicit choice for the track. **Cancel** leaves everything as it was.

A local sheet wins over LRCLIB while it is selected, and the selector, the sheet and the control bar
mark it **Local**. It is read from disk on every play rather than cached, so editing the file in
Notepad takes effect on the next lookup; **Refresh lyrics** re-reads it too. **Automatic (best
match)** in the selector returns the track to LRCLIB without deleting the file.

The import is forgiving about encoding: UTF-8, UTF-16 and Windows-1252 are detected, with or without
a BOM. Other ANSI code pages (Cyrillic, Greek, CJK) are not decoded, so save those files as UTF-8.
A plain-text sheet is accepted and shown as static text, but it cannot follow the music - the
preview says so before you commit.

The header is the search box, in the shape the album art gives it: the album name under the cover,
the song title and the artist are all editable, and **Enter** or **Search** asks lrclib their words.
The album is opt-in as a search term - untouched it stays out of the question, and editing it appends
it - because `artist title` is what finds a song on lrclib. With the fields untouched, **Search** is
the old look-again. Two more escape hatches sit beside Import:

- **Open on LRCLIB** opens `lrclib.net/search/<artist title>` in the browser, for when the browser is
  the better tool than the in-window search.
- **Pin from clipboard** reads an lrclib record id - a bare number, or the digits in a record URL -
  from the clipboard and pins that record.

The complete sheet can **Export…** the timed lyrics in force as an LRC file (CRLF, millisecond
timestamps, the `[ar:]`/`[ti:]`/`[al:]`/`[length:]` header), whether they came from LRCLIB or from an
imported file. Exporting and re-importing reproduces the same timestamps.

A second-language sidecar is optional: save a same-named `.trans.lrc` beside the local sheet (the
lyrics folder is in **Settings → Advanced**), with the translation's own timestamps. Its active line
is drawn under the active lyric in a smaller, dimmer style, and **Settings → Lyrics → Show
translation** turns it off. It applies only to local sheets, because it lives beside the file; a
track resolved from LRCLIB has no folder to put it in. No machine translation is involved: the text
is exactly what the listener supplied.

There is deliberately no automatic "find the `.lrc` next to the audio file": Windows' media controls
expose the title, artist, album and duration, not a path, and a streaming player has no local file to
look beside. Import is an explicit action because that is the only version of it that works for
every player.

If the stored file is deleted from the lyrics folder, the track falls back to the automatic pipeline
and Overlyric says so once, rather than leaving a silent gap.

## The complete lyric sheet

**Lyrics** in the launcher — or the same tab in any window's title bar — opens the whole sheet in its own window: every line, its timestamp, where it came
from (an LRCLIB record id or a local file), and the same badge the selector uses. The line being sung is highlighted and scrolled into
view when the window opens. With plain lyrics the timestamp column is absent rather than filled with
zeroes, because zeroes would claim a timing the sheet does not have.

The sheet itself is read-only: editing it and uploading it to LRCLIB remain the next stage, and both
already have a place to live because the window draws from `LyricSheet` — the parsed lines, the track
details and the record identity in one document — instead of from a formatted string.

The window has one hand-off, because the automatic query is only ever a guess. **Choose lyrics** — the
footer button and the one in the empty state — closes the sheet and opens the selector with the track's
query already in its field, so refining a wrong guess happens in the window built for it. `Ctrl+F`
still opens a single-line field over the sheet for a quick search without leaving it. Both paths pin
the record for the track, and the sheet and the overlay follow immediately.

The selector's field is deliberately small: append and backspace, with **Enter** to search and **Esc**
to leave it. Selection, undo and IME composition are out of scope — it is a query box, not an editor —
but pasting a title from anywhere is the intended way to handle an awkward one. Demo mode does not send
its canned track to lrclib, so searching is disabled there.

While the window is open it follows the track: a new lookup or a different chosen version replaces the
document, and a new record scrolls back to the active line. A new *song* also resets the query, because
the query belongs to the track it was asked about.

## Editing the timing

The complete sheet is also a timing editor. Selecting a line - click it, or press Up/Down - lets you
fix the timing while the song keeps playing:

- **Set start** stamps the selected line with the current playback position; **Set end** does the same
  for its end, which matters across instrumental breaks.
- The nudge buttons move the selected line by 10, 50 or 250 ms. Hold **Shift** while clicking one to
  shift every line instead, for a sheet that is uniformly early or late.
- **Undo** and **Redo** (also `Ctrl+Z` / `Ctrl+Y`) step through up to a hundred edits.
- **Save** writes the edited sheet as this track's local lyric file and selects it, so the overlay
  follows immediately and the timing survives a replay. **Export…** writes the same LRC without
  saving it as the track's sheet.

**Edit file…** hands the track's local `.lrc` to Notepad. Saving there reloads the sheet as soon as the
write lands - the host checks the file's write time on its tick - and a file that no longer parses as
timed lyrics is refused: the previous sheet stays on screen and one tray sentence says why.

The cached LRCLIB answer is never modified: saving produces a local selection, and **Automatic** in
the selector returns the track to LRCLIB while keeping the file. An impossible edit - a start at or
after the line's own end, an end before the start - is refused with a sentence and leaves the sheet
exactly as it was, so a slip never costs the previous timing.

`Ctrl+C` copies the selected line (or the line being sung when nothing is selected), and
`Ctrl+Shift+C` copies the whole current sheet as LRC - handy for a message, a bug report or an
external editor.

The selected line stays marked while playback highlights the line being sung, so "the line I am
editing" and "the line I am hearing" cannot be confused. Choosing a different record replaces the
editable sheet; unsaved edits on the same track are kept until they are saved or the track changes.

## Publishing to LRCLIB

A local sheet - imported or edited - can be published back. **Publish…** appears in the sheet's
footer only when the current sheet is local, because republishing a record the catalogue already
holds would create a duplicate rather than a correction. It opens a confirmation panel with the exact
track fields, the sheet's line count and span, a statement that LRCLIB records are public, and a
reminder that the site's terms and rate limits still apply. Nothing is sent until **Publish** is
pressed; **Cancel** (or Esc) leaves everything untouched.

On confirmation the client asks LRCLIB for a proof-of-work challenge, solves it on every available
core (a few seconds at the normal difficulty, with the attempt count shown in the footer), and sends
exactly one publish request with the single-use token. It never retries with a used token and it never
publishes in the background. A 201 says the record was created; 400, 429 and 503 become sentences
about the token, the rate limit and the server being busy, and a network failure is reported as one.
An existing record is not overwritten: use the duplicate report on LRCLIB instead.

## Refreshing an answer

The tray's **Refresh lyrics** forgets the cached result for the current track — including the copy
stored under a pinned record — and asks LRCLIB again. The version choice is kept; "refresh" means ask
again, not "go back to automatic", and an imported local file is re-read rather than forgotten.
**Settings → Advanced** shows the cache size, the local lyric folder and its size, and can clear
every cached answer at once; clearing the cache never deletes imported files. This is the escape hatch
for a wrong record, or for a lookup that was cached while the network was down.

## Notes on LRCLIB


The client asks the cheapest question that can answer. `/api/get` returns one record for the exact
artist, title, album and duration — about 1.8 kB — while `/api/search` returns up to twenty records
with their full bodies, about 36 kB, twenty times the bytes for one question. So the exact endpoint
goes first and the ranked search is spent only when it did not answer, or answered without timings
(see [Matching the right record](#matching-the-right-record)). A cold track costs one request when
lrclib knows it well, two when the reported album carries a release suffix that has to be cleaned
away, and four when a search has to settle it after both album forms missed; the album-scoped
supplement adds the fifth on the path that needs it. A replay costs nothing.

Four rules keep the client inside the API's terms, all of them checked against the live service:

- **Identify.** Every request carries `User-Agent: Overlyric/<version> (+https://github.com/overlyric/overlyric)`,
  version read from the assembly rather than written into the source. lrclib allows roughly 90
  requests a minute to a client that names itself, and a fraction of that to one that does not.
- **Pace.** Requests go out one at a time, at least 200 ms apart, and never more than fifty in a
  minute — well inside the allowance, and the 200–500 ms spacing the documentation asks for. A
  request that would have to queue longer than five seconds is not sent at all: the answer is
  reported as unknown rather than held hostage behind a queue.
- **Obey `Retry-After`.** On a `429` or a `503` the client stops sending until the window the server
  named has passed, stretched by up to 30 % at random as the documentation asks. This one is not
  theoretical: the service answered in runs of three and four consecutive `503`s while this was
  written, each carrying `Retry-After: 1`, and the previous fixed 600 ms retry walked straight into
  the next one.
- **Do not cascade.** A request that comes back `429`, `5xx`, timed out or refused ends the
  resolution. lrclib's failures are server-wide, so the questions after it would meet the same
  answer; asking them turned one failure into six requests, and a run of skipped tracks into a
  burst. The host retries the track after 15 seconds and once more after 45, which is the schedule
  the documentation asks for and a much cheaper one.

**A transient outage must never become a permanent verdict.** LRCLIB throttles bursts and regularly
answers `503 Server is busy`. Treating that as "this track has no lyrics" and negative-caching it
would hide a track's lyrics after one bad moment. So the client distinguishes three outcomes:

| Response | Meaning | Cached? |
| --- | --- | --- |
| `200` with lyrics | Found | Yes, as lyrics |
| `404`, or a record with no lyrics at all | Definitively absent | Yes, as a tombstone, for 21 days |
| `429`, `5xx`, `400`, `401`, timeout, network error | Unknown — ask again later | **No** |

A `400 ValidationError` belongs in the second group on purpose. lrclib rejects a `duration` outside
1–3600 seconds, so a track longer than an hour — a DJ set, a live prog epic — would send a request
that cannot succeed; the client leaves the parameter out in that case, and treats any other 4xx as
its own bug rather than as a statement about the track. Filing a `400` as "no lyrics" and
tombstoning it, as the client used to, made a permanent verdict out of a rejected parameter.

A tombstone expires after 21 days. lrclib's documentation says missing tracks "may become available
in a later request", because a background service picks them up, so a verdict that never expires
would hide lyrics that exist by now — the one failure this cache must not cause. Three weeks is long
enough that a hopeless track is not re-queried on every replay.

Unsynced lyrics are shown only after the search for a synced sheet of the same length has come up empty:
then single-line mode says `Lyrics not synced` rather than guessing, and multi-line mode scrolls the
text proportionally to track position.

`probe\ResilienceTests` covers the classification, the caching decision, the one-request outage, the
tombstone lifetime and the candidate list (74 checks), against a local stub returning `503`, `429`,
`400`, `404` and lyric-less `200` responses. `probe\ApiBudget` measures the requests themselves and
prints what each resolution path costs.

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