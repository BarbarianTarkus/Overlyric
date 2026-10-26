using System.Runtime.InteropServices;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.Interop;

/// <summary>Reads Unicode text from the Windows clipboard. Best effort by design.</summary>
internal static class Clipboard
{
    /// <summary>
    /// The clipboard's text, or null when another process holds it open or it has no text. Callers
    /// treat null as "nothing was copied" rather than as an error worth a dialog.
    /// </summary>
    public static string? ReadText(nint owner)
    {
        if (!OpenClipboard(owner))
        {
            return null;
        }

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == nint.Zero)
            {
                return null;
            }

            var pointer = GlobalLock(handle);
            if (pointer == nint.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }
}
