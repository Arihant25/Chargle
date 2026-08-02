using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Chargle.Services;

/// <summary>
/// Answers "am I running from an MSIX package?", which changes how startup and file access work.
/// Everything else in the app can then stay identical between the Store build and the portable one.
/// </summary>
public static partial class PackageContext
{
    public static bool IsPackaged { get; } = DetectPackaged();

    private static bool DetectPackaged()
    {
        // Asking for the name with a zero-length buffer is the documented way to probe for
        // identity: APPMODEL_ERROR_NO_PACKAGE (15700) means unpackaged, and any other result
        // (here, ERROR_INSUFFICIENT_BUFFER) means we have it. The name itself is not wanted.
        uint length = 0;
        return GetCurrentPackageFullName(ref length, 0) != 15700;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);
}

public enum StartupAvailability
{
    Enabled,
    Disabled,
    /// <summary>The user turned it off in Task Manager. Only they can turn it back on.</summary>
    BlockedByUser,
    Unsupported,
}

/// <summary>
/// Run-at-login, done the right way for whichever kind of build this is: a StartupTask for the
/// packaged app (so it appears in Settings &gt; Startup apps and the user stays in charge), and a
/// plain HKCU Run entry for the portable one.
/// </summary>
public static class StartupService
{
    private const string TaskId = "ChargleStartupTask";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Chargle";

    public static async Task<StartupAvailability> GetStateAsync()
    {
        if (PackageContext.IsPackaged)
        {
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                return task.State switch
                {
                    StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => StartupAvailability.Enabled,
                    StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy => StartupAvailability.BlockedByUser,
                    _ => StartupAvailability.Disabled,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chargle: startup task unavailable. {ex.Message}");
                return StartupAvailability.Unsupported;
            }
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null ? StartupAvailability.Enabled : StartupAvailability.Disabled;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chargle: could not read the Run key. {ex.Message}");
            return StartupAvailability.Unsupported;
        }
    }

    /// <summary>Returns the state actually achieved, which may not be what was asked for.</summary>
    public static async Task<StartupAvailability> SetAsync(bool enabled)
    {
        if (PackageContext.IsPackaged)
        {
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (!enabled)
                {
                    task.Disable();
                    return StartupAvailability.Disabled;
                }

                var state = await task.RequestEnableAsync();
                return state switch
                {
                    StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => StartupAvailability.Enabled,
                    StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy => StartupAvailability.BlockedByUser,
                    _ => StartupAvailability.Disabled,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chargle: could not change the startup task. {ex.Message}");
                return StartupAvailability.Unsupported;
            }
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return StartupAvailability.Unsupported;

            if (enabled)
            {
                string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Chargle.exe");
                key.SetValue(RunValue, $"\"{exe}\" --background");
                return StartupAvailability.Enabled;
            }

            key.DeleteValue(RunValue, throwOnMissingValue: false);
            return StartupAvailability.Disabled;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Chargle: could not change the Run key. {ex.Message}");
            return StartupAvailability.Unsupported;
        }
    }
}
