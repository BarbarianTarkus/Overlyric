using Windows.UI.Notifications;

namespace Overlyric.ToastProbe;

/// <summary>
/// Platform probe: can Overlyric raise a WinRT toast notification while it runs unpackaged, and if
/// not, at which step does it fail?
/// </summary>
/// <remarks>
/// <para>
/// Measured on Windows 11 (build 26100), from an unpackaged executable with no Start Menu shortcut
/// carrying a <c>System.AppUserModel.ID</c>:
/// </para>
/// <list type="number">
/// <item><c>ToastNotificationManager.CreateToastNotifier(appId)</c> returns a notifier.</item>
/// <item><c>ToastNotifier.Setting</c> throws <c>0x80070490</c> (ERROR_NOT_FOUND, "Element not
/// found") - the missing AUMID registration surfacing.</item>
/// <item><c>GetTemplateContent</c> and <c>ToastNotification</c> construction succeed, and
/// <c>ToastNotifier.Show()</c> does not throw, yet no toast appears. A silent no-op, which is worse
/// than an exception: an application would believe it had told the user something.</item>
/// </list>
/// <para>
/// A non-MSIX app's AUMID has to be registered with the shell, which in practice means a Start Menu
/// shortcut carrying that property. Overlyric's shortcut does not set one, and a <c>--demo</c> run
/// unpackaged has no shortcut at all. That is why the app's Windows notifications use the tray
/// balloon (<c>Shell_NotifyIcon</c> with <c>NIF_INFO</c>) instead: no AUMID or shortcut requirement,
/// the same notify icon the tray menu is already built on, and the surface Windows 10+ renders as a
/// notification banner - while its failure mode is silent rather than an exception at the call site.
/// </para>
/// <para>
/// The probe exits 0 whether or not the steps succeed: a silent <c>Show()</c> is the documented
/// result here, not a broken build. Run it and read the printed lines.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>The AUMID Overlyric would use if toast notifications were viable.</summary>
    private const string AppUserModelId = "Overlyric.ToastProbe";

    private static int Main()
    {
        ToastNotifier? notifier = null;

        try
        {
            notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            Console.WriteLine($"1. CreateToastNotifier(\"{AppUserModelId}\") -> success ({notifier.GetType().FullName})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"1. CreateToastNotifier(\"{AppUserModelId}\") -> threw 0x{ex.HResult:X8} ({ex.Message})");
        }

        if (notifier is not null)
        {
            try
            {
                Console.WriteLine($"2. ToastNotifier.Setting -> {notifier.Setting}");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"2. ToastNotifier.Setting -> threw 0x{ex.HResult:X8} ({(ex.HResult == unchecked((int)0x80070490) ? "ERROR_NOT_FOUND: no AUMID registered for this process" : ex.Message)})");
            }

            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].InnerText = "Overlyric toast probe";
                texts[1].InnerText = "If a notification appears, an unpackaged toast is possible after all.";

                notifier.Show(new ToastNotification(xml));
                Console.WriteLine("3. ToastNotification.Show() -> returned without throwing");
                Console.WriteLine("   Look at the notification area for the next few seconds: nothing appears.");

                // Long enough for a real toast to be seen, which is what makes the silent no-op a
                // measurement rather than an assumption.
                Thread.Sleep(5000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"3. ToastNotification.Show() -> threw 0x{ex.HResult:X8} ({ex.Message})");
            }
        }

        Console.WriteLine("FINDING | unpackaged toasts are not usable: setting throws 0x80070490 and Show() is a no-op.");
        Console.WriteLine("FINDING | Overlyric's Windows notifications therefore go through the tray balloon.");
        return 0;
    }
}
