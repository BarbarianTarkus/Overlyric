namespace Overlyric.Media;

/// <summary>
/// One entry in the user's source priority list: which application, and what to call it.
/// </summary>
/// <remarks>
/// The label is stored rather than looked up, because the list has to stay readable when that player
/// is closed, and because the identity is an app user model id - <c>Brave._crx_cimpffimgejohpbehjkcdpjolg</c>
/// is not a name anyone recognises.
/// </remarks>
internal sealed class SourcePreference
{
    /// <summary>App user model id, compared case-insensitively throughout.</summary>
    public string Aumid { get; set; } = "";

    /// <summary>What the menus show for this entry.</summary>
    public string Label { get; set; } = "";

    public SourcePreference Clone() => new() { Aumid = Aumid, Label = Label };
}

/// <summary>
/// A session the selector may pick, reduced to the facts the choice depends on.
/// <see cref="Key"/> identifies it within one evaluation, because two sessions can share an app user
/// model id and the selector still has to say which one it picked.
/// </summary>
internal readonly record struct SourceCandidate(
    string Key,
    string Aumid,
    string Label,
    bool IsSystemCurrent,
    bool IsPlaying,
    bool PositionAdvances);

/// <summary>Why a source was picked. Shown in the menus, and the reason a bug report makes sense.</summary>
internal enum SourceChoiceReason
{
    /// <summary>Nothing is publishing, so there is nothing to follow.</summary>
    None,

    /// <summary>The player the user pinned outranks everything, which is what pinning means.</summary>
    Pinned,

    /// <summary>The first entry in the priority list that is running.</summary>
    Priority,

    /// <summary>Nothing configured matched, so the session Windows calls current was taken.</summary>
    SystemCurrent,

    /// <summary>Nothing matched and Windows named nothing usable, so the best remaining candidate was taken.</summary>
    Fallback
}

internal readonly record struct SourceChoice(string Key, string Aumid, SourceChoiceReason Reason)
{
    public static readonly SourceChoice None = new("", "", SourceChoiceReason.None);

    public bool HasChoice => Reason != SourceChoiceReason.None;
}

/// <summary>
/// The two halves of the user's source setting, read fresh on every poll so a change in the settings
/// window or the tray menu takes effect on the next tick without a restart.
/// </summary>
internal readonly record struct SourceSettings(IReadOnlyList<SourcePreference> Priority, string PinnedAumid)
{
    public static readonly SourceSettings Default = new([], "");
}

/// <summary>
/// Decides which of several publishing players the overlay should follow.
/// </summary>
/// <remarks>
/// Windows has no API for this. <c>GetCurrentSession()</c> is the session the system believes the user
/// would most likely want to control - the one the media keys apply to - which is a guess about intent
/// and not a fact about audio; measured with <c>probe\SmtcProbe --sessions</c> it will happily sit on a
/// browser video whose timeline has been frozen for an hour while a music player is also running.
/// <para>
/// So the order is: the pinned player, then the user's priority list in order, then Windows' own
/// answer, then whatever is left. A pin deliberately beats a playing source: it is the user saying
/// "this one", and second-guessing that is how the setting stops being trustable. Nothing in the list
/// matching falls back to Windows rather than to silence, so configuring a list can never leave the
/// overlay with nothing to do.
/// </para>
/// <para>
/// Position advancement is never a filter here, only a tie-break between sessions of the same
/// application. Whether a source is worth following at all is a separate question, and answering it by
/// silently dropping the user's choice would be the wrong place to be clever.
/// </para>
/// </remarks>
internal static class SourceSelector
{
    /// <summary>
    /// How many players the priority list holds. A bound rather than a scrollbar: the settings window
    /// draws a fixed grid of rows and has nowhere to scroll, so the list has to be a fixed shape or the
    /// layout breaks. Six is more players than anyone runs at once.
    /// </summary>
    public const int MaxPriority = 6;

    /// <summary>The whole rule, from the settings the user configured.</summary>
    public static SourceChoice Choose(IReadOnlyList<SourceCandidate> candidates, SourceSettings settings) =>
        Choose(candidates, settings.Priority, settings.PinnedAumid);

    public static SourceChoice Choose(
        IReadOnlyList<SourceCandidate> candidates,
        IReadOnlyList<SourcePreference>? priority,
        string? pinnedAumid)
    {
        if (candidates.Count == 0)
        {
            return SourceChoice.None;
        }

        if (!string.IsNullOrWhiteSpace(pinnedAumid) && Best(candidates, pinnedAumid) is { } pinned)
        {
            return new SourceChoice(pinned.Key, pinned.Aumid, SourceChoiceReason.Pinned);
        }

        if (priority is not null)
        {
            foreach (var entry in priority)
            {
                if (entry is null)
                {
                    continue;
                }

                if (Best(candidates, entry.Aumid) is { } wanted)
                {
                    return new SourceChoice(wanted.Key, wanted.Aumid, SourceChoiceReason.Priority);
                }
            }
        }

        if (First(candidates, c => c.IsSystemCurrent) is { } current)
        {
            return new SourceChoice(current.Key, current.Aumid, SourceChoiceReason.SystemCurrent);
        }

        var fallback = First(candidates, c => c.IsPlaying && c.PositionAdvances)
            ?? First(candidates, c => c.IsPlaying)
            ?? candidates[0];

        return new SourceChoice(fallback.Key, fallback.Aumid, SourceChoiceReason.Fallback);
    }

    /// <summary>
    /// Cleans a list that came from a hand-editable file: no blanks, no duplicates, and every entry
    /// named. Called on load, so a broken file degrades rather than throwing.
    /// </summary>
    public static List<SourcePreference> NormalisePriority(IEnumerable<SourcePreference>? priority)
    {
        var result = new List<SourcePreference>();
        if (priority is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in priority)
        {
            if (entry is null)
            {
                continue;
            }

            var aumid = (entry.Aumid ?? "").Trim();
            if (aumid.Length == 0 || !seen.Add(aumid))
            {
                continue;
            }

            var label = (entry.Label ?? "").Trim();
            result.Add(new SourcePreference { Aumid = aumid, Label = label.Length == 0 ? aumid : label });
        }

        if (result.Count > MaxPriority)
        {
            result.RemoveRange(MaxPriority, result.Count - MaxPriority);
        }

        return result;
    }

    /// <summary>Moves an entry one place towards the front, and answers where it ended up.</summary>
    public static int MoveUp(List<SourcePreference> priority, int index)
    {
        if (index <= 0 || index >= priority.Count)
        {
            return index;
        }

        (priority[index - 1], priority[index]) = (priority[index], priority[index - 1]);
        return index - 1;
    }

    /// <summary>Moves an entry one place towards the back, and answers where it ended up.</summary>
    public static int MoveDown(List<SourcePreference> priority, int index)
    {
        if (index < 0 || index >= priority.Count - 1)
        {
            return index;
        }

        (priority[index + 1], priority[index]) = (priority[index], priority[index + 1]);
        return index + 1;
    }

    public static bool Remove(List<SourcePreference> priority, string aumid)
    {
        var index = IndexOf(priority, aumid);
        if (index < 0)
        {
            return false;
        }

        priority.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Puts an application at the front of the list, adding it if it was not there. Adding from a menu
    /// means "prefer this", so it goes to the top rather than to the end.
    /// </summary>
    public static void Promote(List<SourcePreference> priority, string aumid, string label)
    {
        var trimmed = (aumid ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var index = IndexOf(priority, trimmed);
        if (index >= 0)
        {
            var existing = priority[index];
            priority.RemoveAt(index);
            priority.Insert(0, existing);
            return;
        }

        var name = (label ?? "").Trim();
        priority.Insert(0, new SourcePreference { Aumid = trimmed, Label = name.Length == 0 ? trimmed : name });
    }

    public static int IndexOf(List<SourcePreference> priority, string? aumid) =>
        priority.FindIndex(e => SameApp(e.Aumid, aumid));

    private static bool SameApp(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The session to use for one application. Two sessions can share an app user model id - two
    /// browser tabs - and the one actually playing, with a timeline that moves, is the one meant.
    /// </summary>
    private static SourceCandidate? Best(IReadOnlyList<SourceCandidate> candidates, string? aumid)
    {
        SourceCandidate? best = null;

        foreach (var candidate in candidates)
        {
            if (!SameApp(candidate.Aumid, aumid))
            {
                continue;
            }

            if (best is null || Rank(candidate) > Rank(best.Value))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static int Rank(SourceCandidate candidate) =>
        candidate.IsPlaying && candidate.PositionAdvances ? 3
        : candidate.IsPlaying ? 2
        : candidate.IsSystemCurrent ? 1
        : 0;

    private static SourceCandidate? First(IReadOnlyList<SourceCandidate> candidates, Func<SourceCandidate, bool> predicate)
    {
        foreach (var candidate in candidates)
        {
            if (predicate(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
