using static Overlyric.Interop.NativeMethods;

namespace Overlyric.Interop;

/// <summary>
/// The Overlyric icon, read from the executable's own resources.
/// </summary>
/// <remarks>
/// <c>Overlyric.csproj</c> points <c>&lt;ApplicationIcon&gt;</c> at Assets\Overlyric.ico, and the
/// toolchain gives that icon a fixed resource id. Loading it back out - rather than drawing a
/// second copy into the process - keeps the notification area, the window class icons and the file
/// in Explorer the same picture by construction.
///
/// Each size is loaded once and kept for the process lifetime. The returned HICONs are owned by the
/// process, so they are deliberately never destroyed: the shell may still be holding one when
/// shutdown begins, and the process is about to exit anyway.
/// </remarks>
internal static class AppIcon
{
    /// <summary>
    /// IDI_APPLICATION. The Windows resource compiler stamps the executable's icon with this id, so
    /// the icon Explorer shows for Overlyric.exe is the one loaded here.
    /// </summary>
    private const int ResourceId = 32512;

    private static nint _small;
    private static nint _large;

    /// <summary>
    /// Notification-area and title-bar size for the current DPI, or <c>nint.Zero</c> when the
    /// executable carries no icon (a build without the project's <c>ApplicationIcon</c>).
    /// </summary>
    public static nint Small
    {
        get
        {
            if (_small == nint.Zero)
            {
                _small = Load(GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON));
            }

            return _small;
        }
    }

    /// <summary>Alt+Tab and large-icon size, or <c>nint.Zero</c> when the icon cannot be loaded.</summary>
    public static nint Large
    {
        get
        {
            if (_large == nint.Zero)
            {
                _large = Load(GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON));
            }

            return _large;
        }
    }

    private static nint Load(int width, int height) =>
        LoadImageW(
            GetModuleHandleW(null),
            new nint(ResourceId),
            IMAGE_ICON,
            width,
            height,
            LR_DEFAULTCOLOR);
}
