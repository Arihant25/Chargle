using System.Diagnostics;
using System.Text;

namespace Chargle.Services;

/// <summary>
/// Catches what would otherwise be a silent death.
///
/// Chargle runs at login and lives in the tray, which is the worst possible shape for an
/// unhandled exception: the process disappears, nothing is shown, and the next time the user
/// plugs in their charger they just notice it stopped working and have no idea why. So every
/// failure is written somewhere they can find it, and anything survivable is survived.
/// </summary>
public static class CrashLog
{
    private static readonly Lock Gate = new();
    private const int MaxBytes = 256 * 1024;

    public static string Path => System.IO.Path.Combine(AppPaths.DataDirectory, "crash.log");

    public static void Write(string source, Exception? exception, bool fatal)
    {
        var text = new StringBuilder()
            .AppendLine($"---- {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {source}  {(fatal ? "fatal" : "recovered")}")
            .AppendLine(exception?.ToString() ?? "(no exception object)")
            .AppendLine();

        Debug.WriteLine(text.ToString());

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);

                // Truncate rather than grow without limit. A crash that repeats on every power
                // event would otherwise quietly fill the disk.
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                    File.Delete(Path);

                File.AppendAllText(Path, text.ToString());
            }
        }
        catch (Exception ex)
        {
            // If even this fails there is nowhere left to complain to.
            Debug.WriteLine($"Chargle: could not write the crash log. {ex.Message}");
        }
    }
}
