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
    /// <summary>
    /// Replaces the clipboard with <paramref name="text"/>. The memory handle is handed to Windows on
    /// success and must not be freed afterwards; on failure it is released here.
    /// </summary>
    public static bool WriteText(nint owner, string text)
    {
        if (!OpenClipboard(owner))
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            var bytes = (text.Length + 1) * sizeof(char);
            var memory = Marshal.AllocHGlobal(bytes);

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, memory, text.Length);
                Marshal.WriteInt16(memory, text.Length * sizeof(char), 0);

                if (SetClipboardData(CF_UNICODETEXT, memory) == nint.Zero)
                {
                    return false;
                }

                memory = nint.Zero;
                return true;
            }
            finally
            {
                if (memory != nint.Zero)
                {
                    Marshal.FreeHGlobal(memory);
                }
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

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
