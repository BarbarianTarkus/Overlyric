namespace Overlyric.UiTests;

/// <summary>
/// The settings window's layout, in DIP client coordinates.
/// </summary>
/// <remarks>
/// These numbers mirror <c>SettingsWindow</c>: a 780x584 client at 96 DPI, a 44 DIP title bar, rows
/// starting at content y 132 with 46 DIP of row height, a 196 DIP navigation column and 30 DIP of
/// padding. The suite drives the same geometry rather than guessing from a screenshot, and the
/// screenshot is what proves the mirror is still right. A capture read at the wrong coordinate would
/// still produce a plus-shaped smear, but it would also change the next section's baseline.
/// </remarks>
internal static class SettingsGeometry
{
    public const int ClientWidthDip = 780;
    public const int ClientHeightDip = 584;

    private const int TitleHeightDip = 44;
    private const int RowHeightDip = 46;
    private const int RowsTopDip = 132;
    private const int ContentLeft = 226;
    private const int ContentRight = 750;

    public const int NavX = 98;

    // --- The shared Window menu in the 44 DIP title bar -------------------------------------------
    // The same four destinations, at the same x, in every window (WindowMenu.cs). Clicking one
    // swaps the page in place: the page you were on hides and the next takes its rectangle.
    public const int MenuY = 22;
    public const int MenuLyricsX = 80;
    public const int MenuVersionsX = 160;
    public const int MenuDiagnosticsX = 262;
    public const int MenuSettingsX = 360;

    /// <summary>The eight navigation entries, in the order the window lists them.</summary>
    public static readonly (string Name, int ClientY)[] NavEntries =
    [
        ("lyrics", 155),
        ("appearance", 197),
        ("timing", 239),
        ("position", 281),
        ("behaviour", 323),
        ("players", 389),
        ("shortcuts", 431),
        ("advanced", 473)
    ];

    /// <summary>Row 4 of the Lyrics section: the Karaoke sweep switch.</summary>
    public const int KaraokeToggleRow = 4;

    /// <summary>Row 2 of the Lyrics section: the active lyric size stepper.</summary>
    public const int LyricSizeRow = 2;

    /// <summary>The step the window applies to <c>ActiveFontSize</c> per press.</summary>
    public const double LyricSizeStep = 2.0;

    private static int ClientY(int contentY) => TitleHeightDip + contentY;

    private static int RowTop(int row) => RowsTopDip + (row * RowHeightDip);

    /// <summary>Centre of a row's switch. The hit rectangle is the track plus an 8 DIP margin.</summary>
    public static (int X, int Y) Toggle(int row) => (727, ClientY(RowTop(row) + (RowHeightDip / 2)));

    /// <summary>Centre of a row's "+" button. The stepper's three cells are 30/84/30 with 6 DIP gaps.</summary>
    public static (int X, int Y) StepperPlus(int row) => (735, ClientY(RowTop(row) + (RowHeightDip / 2)));

    /// <summary>Centre of a row's "-" button.</summary>
    public static (int X, int Y) StepperMinus(int row) => (609, ClientY(RowTop(row) + (RowHeightDip / 2)));

    // --- Masks -----------------------------------------------------------------------------------

    /// <summary>
    /// The second line of a two-line row. The window draws it with live values (the audio endpoint's
    /// name, the cache size, the foreground app), which belong to the machine rather than to the UI.
    /// </summary>
    public static MaskRect Note(int row) => new(ContentLeft, ClientY(RowTop(row) + 20), ContentRight - ContentLeft, 20);

    /// <summary>The footer prints the product version, which changes with every release.</summary>
    public static MaskRect Footer() => new(190, 546, 570, 30);

    /// <summary>A whole row band, for a row whose text and buttons both follow live system state.</summary>
    public static MaskRect RowBand(int row) => new(ContentLeft, ClientY(RowTop(row)), ContentRight - ContentLeft, RowHeightDip);

    /// <summary>The Players status value ("Automatic" or the pinned player's friendly name).</summary>
    public static MaskRect PlayersStatusValue() => new(460, ClientY(RowTop(0)), 236, RowHeightDip);

    /// <summary>The Players pin switch: its enabled state depends on whether anything is publishing.</summary>
    public static MaskRect PlayersPinToggle() => new(690, ClientY(RowTop(0)), 72, RowHeightDip);

    /// <summary>How many rows the Shortcuts section lists (mirrors <c>ShortcutActions.All</c>).</summary>
    public const int ShortcutChipCount = 9;

    /// <summary>
    /// The text band of a shortcut chip. A chip is painted in the window's warning colour when its
    /// chord is already owned somewhere in the session - by the production application, by another
    /// test instance, by any program that registered the combination. That is machine state, not
    /// this window's design, so only the text band is masked; the chip's background, size and
    /// position stay compared.
    /// </summary>
    public static MaskRect ChipText(int index) => new(ContentRight - 184, ClientY(RowTop(index) + 14), 178, 20);

    /// <summary>
    /// What may differ in one section's capture, and why. Everything not listed here is expected to
    /// be identical run to run and build to build; if it moves, that is the bug the baseline catches.
    /// </summary>
    public static IReadOnlyList<MaskRect> MasksForSection(string section) => section switch
    {
        // Timing row 0 prints the default audio endpoint's friendly name, which is the user's
        // hardware, not the application.
        "timing" => [Note(0), Footer()],

        // Behaviour row 6 prints "none · <foreground app>"; which app owns the foreground at that
        // moment is a property of the machine (and, on a private desktop, of the run itself).
        "behaviour" => [Note(6), Footer()],

        // Players row 0 names the pinned player and row 7 offers to add whatever is publishing to
        // Windows right now. Both follow live media sessions, so a track open on the host would
        // otherwise rewrite this baseline. The priority rows themselves come from the fixture.
        "players" => [PlayersStatusValue(), PlayersPinToggle(), RowBand(7), Footer()],

        // Advanced rows 1 and 2 print the lyric cache and local library sizes; the versions footer.
        "advanced" => [Note(1), Note(2), Footer()],

        // Shortcut chips show the fixture's chords, but their colour reports whether the session
        // already owns that chord; a second instance of the suite or another application holding it
        // would otherwise fail this baseline.
        "shortcuts" => [.. Enumerable.Range(0, ShortcutChipCount).Select(ChipText), Footer()],

        // Lyrics, Appearance and Position are entirely fixture- and version-driven; only the footer
        // moves, and only when the product version does.
        _ => [Footer()]
    };
}
