using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Chargle.Services;

/// <summary>
/// Answers whether now is a reasonable moment to make a noise.
///
/// There is no single API for this, which is annoying, so three signals are combined. Each was
/// checked against a real machine rather than taken from documentation, because the documentation
/// does not quite say what it appears to say.
/// </summary>
public static partial class Presence
{
    private const int QunsNotPresent = 1;
    private const int QunsAcceptsNotifications = 5;

    public static bool ShouldStayQuiet() => IsDoNotDisturbOn() || IsFocusSessionActive() || IsShellBusy();

    /// <summary>
    /// Windows 11's Do Not Disturb toggle, read from where the shell actually keeps it.
    ///
    /// Neither documented API reports this. <c>SHQueryUserNotificationState</c> predates Do Not
    /// Disturb and returns QUNS_ACCEPTS_NOTIFICATIONS with it switched on, and
    /// <c>FocusSessionManager.IsFocusActive</c> only covers timed Focus sessions. Both were
    /// observed staying false while Do Not Disturb was toggled on Windows 11 build 28020.
    ///
    /// What does track it is a CloudStore blob naming the active quiet hours profile:
    /// "Unrestricted" while notifications are allowed, "PriorityOnly" or "AlarmsOnly" while they
    /// are not. That was confirmed by watching the value change as the toggle was flipped.
    /// </summary>
    public static bool IsDoNotDisturbOn()
    {
        try
        {
            const string root =
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current";

            using var current = Registry.CurrentUser.OpenSubKey(root);

            // The container is prefixed with a GUID with no documented guarantee behind it, so
            // it is matched by suffix rather than hard coded.
            string? container = current?.GetSubKeyNames()
                .FirstOrDefault(n => n.EndsWith("windows.data.donotdisturb.quiethourssettings",
                    StringComparison.OrdinalIgnoreCase));
            if (container is null) return false;

            using var settings = current!.OpenSubKey(
                $@"{container}\windows.data.donotdisturb.quiethourssettings");
            if (settings?.GetValue("Data") is not byte[] data) return false;

            string? profile = FindProfileName(data);
            if (profile is null) return false;

            return !profile.EndsWith("Unrestricted", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Older Windows does not have this key, which is not an error worth reacting to.
            Debug.WriteLine($"Chargle: could not read the Do Not Disturb state. {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Pulls the profile name out of the blob, trying both byte alignments.
    ///
    /// The alignment matters and cost an hour. The name is UTF-16 but sits at whatever offset
    /// the surrounding binary format leaves it at, which on the machine this was written on is
    /// an odd one. Decoding the buffer from zero shifts every character by a byte and produces
    /// nothing but garbage, which looks exactly like the feature being switched off.
    /// </summary>
    private static string? FindProfileName(byte[] data)
    {
        const string marker = "Microsoft.QuietHoursProfile";

        foreach (int start in stackalloc[] { 0, 1 })
        {
            if (data.Length - start < 2) continue;

            string text = Encoding.Unicode.GetString(data, start, (data.Length - start) & ~1);
            int at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            var name = new StringBuilder();
            for (int i = at; i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.'); i++)
                name.Append(text[i]);

            return name.ToString();
        }

        return null;
    }

    /// <summary>A timed Focus session, which is a separate thing from the Do Not Disturb toggle.</summary>
    private static bool IsFocusSessionActive()
    {
        try
        {
            return Windows.UI.Shell.FocusSessionManager.IsSupported
                && Windows.UI.Shell.FocusSessionManager.GetDefault().IsFocusActive;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chargle: could not read the focus session state. {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Full-screen apps, presentation mode and screen sharing. This is what
    /// <c>SHQueryUserNotificationState</c> is still genuinely good for.
    /// </summary>
    private static bool IsShellBusy()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0) return false;

            // Anything other than "nobody is here" or "go ahead" means someone is busy:
            // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE, QUNS_QUIET_TIME,
            // and QUNS_APP (a store app running full screen).
            return state is not (QunsNotPresent or QunsAcceptsNotifications);
        }
        catch (Exception ex)
        {
            // If the shell will not answer, err towards making the sound the user asked for.
            Debug.WriteLine($"Chargle: could not query notification state. {ex.Message}");
            return false;
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}
