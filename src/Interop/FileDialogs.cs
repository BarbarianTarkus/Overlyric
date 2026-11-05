using System.Runtime.InteropServices;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.Interop;

/// <summary>
/// The two OS file pickers the lyric library needs: import a sheet, export a sheet. The common
/// dialogs are used instead of <c>IFileOpenDialog</c> because they need no COM object of their own
/// and no shell namespace code - the filter, the owner and the buffer are the whole contract.
/// </summary>
internal static class FileDialogs
{
    /// <summary>Long enough for an extended path; the dialog writes UTF-16 code units here.</summary>
    private const int MaxPath = 4096;

    private const uint OFN_OVERWRITEPROMPT = 0x00000002;
    private const uint OFN_NOCHANGEDIR = 0x00000008;
    private const uint OFN_PATHMUSTEXIST = 0x00000800;
    private const uint OFN_FILEMUSTEXIST = 0x00001000;
    private const uint OFN_EXPLORER = 0x00080000;

    private const string ImportFilter = "Lyric files\0*.lrc;*.txt\0All files\0*.*\0\0";
    private const string ExportFilter = "Lyric files\0*.lrc\0All files\0*.*\0\0";

    /// <summary>Shows the import dialog; returns the chosen path, or null when it was cancelled.</summary>
    public static string? OpenLyricFile(nint owner) =>
        Show(owner, "Import lyrics", ImportFilter, null, defaultExtension: null, OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR, save: false);

    /// <summary>Shows the export dialog; returns the chosen path, or null when it was cancelled.</summary>
    public static string? SaveLyricFile(nint owner, string suggestedName) =>
        Show(owner, "Export lyrics", ExportFilter, suggestedName, defaultExtension: "lrc", OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR, save: true);

    private static string? Show(
        nint owner,
        string title,
        string filter,
        string? initialName,
        string? defaultExtension,
        uint flags,
        bool save)
    {
        var fileBuffer = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        var filterBuffer = Marshal.StringToHGlobalUni(filter);
        var titleBuffer = Marshal.StringToHGlobalUni(title);
        nint extensionBuffer = nint.Zero;

        try
        {
            Marshal.WriteInt16(fileBuffer, 0);

            if (!string.IsNullOrEmpty(initialName))
            {
                var name = initialName.Replace('"', '_');
                var chars = name.ToCharArray();
                var length = Math.Min(chars.Length, MaxPath - 1);
                Marshal.Copy(chars, 0, fileBuffer, length);
                Marshal.WriteInt16(fileBuffer, length * sizeof(char), 0);
            }

            if (!string.IsNullOrEmpty(defaultExtension))
            {
                extensionBuffer = Marshal.StringToHGlobalUni(defaultExtension);
            }

            var dialog = new OpenFileName
            {
                StructSize = (uint)Marshal.SizeOf<OpenFileName>(),
                Owner = owner,
                Filter = filterBuffer,
                FilterIndex = 1,
                File = fileBuffer,
                MaxFile = MaxPath,
                Title = titleBuffer,
                Flags = flags,
                DefaultExtension = extensionBuffer
            };

            var accepted = save ? GetSaveFileNameW(ref dialog) : GetOpenFileNameW(ref dialog);

            return accepted ? Marshal.PtrToStringUni(fileBuffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
            Marshal.FreeHGlobal(filterBuffer);
            Marshal.FreeHGlobal(titleBuffer);

            if (extensionBuffer != nint.Zero)
            {
                Marshal.FreeHGlobal(extensionBuffer);
            }
        }
    }
}
