using Microsoft.Win32;

namespace Overlyric.App;

/// <summary>
/// The per-user "start with Windows" registration. The Run key is the smallest mechanism that needs
/// no elevation, no service and no scheduled task; it is also the one the uninstaller cannot know
/// about, which is why the settings hint says where the value lives.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Overlyric";

    /// <summary>Write or remove the Run value. Returns false when the registry refused.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return false;
            }

            key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception)
        {
            // A locked-down profile simply keeps the toggle off; it must not stop the app.
            return false;
        }
    }
}
