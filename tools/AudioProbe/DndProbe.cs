using Chargle.Services;

namespace Chargle.AudioProbe;

/// <summary>
/// Prints what the app's own quiet-hours logic decides, plus the raw signals behind it.
///
///     dotnet run --project tools/AudioProbe -- --dnd
///
/// This links the real <see cref="Presence"/> source rather than a copy, so it is a check of the
/// shipping behaviour. Toggle Do Not Disturb and run it again: the verdict has to follow.
/// </summary>
public static class DndProbe
{
    public static int Run()
    {
        Console.WriteLine("=== what Chargle decides ===");
        Console.WriteLine($"  ShouldStayQuiet   {Presence.ShouldStayQuiet()}");
        Console.WriteLine($"  IsDoNotDisturbOn  {Presence.IsDoNotDisturbOn()}");

        Console.WriteLine();
        Console.WriteLine("=== the signals underneath ===");

        try
        {
            bool supported = Windows.UI.Shell.FocusSessionManager.IsSupported;
            Console.WriteLine($"  FocusSessionManager.IsSupported    {supported}");
            if (supported)
            {
                Console.WriteLine(
                    $"  FocusSessionManager.IsFocusActive  {Windows.UI.Shell.FocusSessionManager.GetDefault().IsFocusActive}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FocusSessionManager failed: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("  Neither FocusSessionManager nor SHQueryUserNotificationState tracks the");
        Console.WriteLine("  Do Not Disturb toggle. Both were observed staying false with it switched on.");

        return 0;
    }
}
