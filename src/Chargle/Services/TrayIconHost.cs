using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Chargle.Services;

/// <summary>
/// The tray icon, talking to Shell_NotifyIcon directly.
///
/// Chargle spends almost all of its life as this icon and nothing else, so it is worth doing
/// properly rather than depending on a wrapper. In particular it handles TaskbarCreated: when
/// Explorer restarts, every tray icon in the system is destroyed, and applications that do not
/// listen for that broadcast simply vanish from the tray until they are restarted. It is the
/// single most common way a tray app quietly breaks.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const int WmDestroy = 0x0002;
    private const int WmCommand = 0x0111;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmTrayCallback = 0x0400 + 1; // WM_APP + 1

    private const int IdOpen = 1;
    private const int IdTest = 2;
    private const int IdMute = 3;
    private const int IdQuit = 4;

    private readonly App _app;
    private readonly WndProc _wndProc; // held so the GC cannot collect the callback
    private readonly uint _taskbarCreatedMessage;
    private readonly ushort _classAtom;

    private nint _hwnd;
    private nint _icon;
    private bool _added;
    private bool _disposed;

    public TrayIconHost(App app)
    {
        _app = app;
        _wndProc = HandleMessage;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "ChargleTrayWindow",
        };

        _classAtom = RegisterClassEx(ref wc);
        if (_classAtom == 0)
            throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()}).");

        // HWND_MESSAGE: a window that exists only to receive messages. It is never shown, never
        // appears in Alt+Tab, and survives the main window being closed, which matters because
        // closing the window is the normal way to use this app.
        _hwnd = CreateWindowEx(
            0, "ChargleTrayWindow", "Chargle", 0, 0, 0, 0, 0,
            HwndMessage, 0, GetModuleHandle(null), 0);

        if (_hwnd == 0)
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");

        _icon = LoadTrayIcon();
        AddIcon();
    }

    private static nint HwndMessage => -3;

    /// <summary>Updates the hover text, so the tray itself reports the current state.</summary>
    public void SetToolTip(string text)
    {
        if (_disposed || !_added) return;

        var data = CreateData();
        data.uFlags = NifTip;
        data.szTip = Truncate(text, 127);
        Shell_NotifyIcon(NimModify, ref data);
    }

    private nint LoadTrayIcon()
    {
        // Loading at the system's small-icon size rather than letting the shell scale a large
        // one is what keeps the glyph crisp at 100% and correct at 200%.
        int width = GetSystemMetrics(SmCxSmIcon);
        int height = GetSystemMetrics(SmCySmIcon);

        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Chargle.ico");
        nint icon = LoadImage(0, path, ImageIcon, width, height, LrLoadFromFile);

        if (icon == 0)
            Debug.WriteLine($"Chargle: could not load the tray icon from {path}.");

        return icon;
    }

    private void AddIcon()
    {
        var data = CreateData();
        data.uFlags = NifIcon | NifMessage | NifTip;
        data.uCallbackMessage = WmTrayCallback;
        data.hIcon = _icon;
        data.szTip = "Chargle";

        _added = Shell_NotifyIcon(NimAdd, ref data);
        if (!_added)
            Debug.WriteLine($"Chargle: Shell_NotifyIcon add failed ({Marshal.GetLastWin32Error()}).");
    }

    private NotifyIconData CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
    };

    private nint HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // Explorer restarted and took every tray icon with it. Put ours back.
        if (msg == _taskbarCreatedMessage)
        {
            _added = false;
            AddIcon();
            return 0;
        }

        switch (msg)
        {
            case WmTrayCallback:
                OnTrayEvent((int)(lParam & 0xFFFF));
                return 0;

            case WmCommand:
                OnMenuCommand((int)(wParam & 0xFFFF));
                return 0;

            case WmDestroy:
                return 0;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnTrayEvent(int mouseMessage)
    {
        switch (mouseMessage)
        {
            case WmLButtonUp:
            case WmLButtonDblClk:
                Post(() => _app.ShowMainWindow());
                break;

            case WmRButtonUp:
                ShowMenu();
                break;
        }
    }

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;

        try
        {
            bool muted = _app.Settings.Current.IsMutedNow;

            AppendMenu(menu, MfString, IdOpen, "Open Chargle");
            AppendMenu(menu, MfString, IdTest, "Play the connect sound");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString | (muted ? MfChecked : 0), IdMute,
                muted ? "Muted, click to unmute" : "Mute for an hour");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, IdQuit, "Quit");

            GetCursorPos(out var point);

            // Required by the docs and genuinely necessary: without it the menu does not dismiss
            // when the user clicks elsewhere, and sits there until it is clicked directly.
            SetForegroundWindow(_hwnd);

            TrackPopupMenuEx(menu, TpmRightButton | TpmRightAlign | TpmBottomAlign,
                point.X, point.Y, _hwnd, 0);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void OnMenuCommand(int id)
    {
        switch (id)
        {
            case IdOpen:
                Post(() => _app.ShowMainWindow());
                break;

            case IdTest:
                Post(() =>
                {
                    var pack = _app.Library.Find(_app.Settings.Current.PackId)
                               ?? _app.Library.Packs.FirstOrDefault();
                    if (pack is not null) _app.Watcher.Preview(pack, Cue.Plug);
                });
                break;

            case IdMute:
                bool muted = _app.Settings.Current.IsMutedNow;
                _app.Settings.Update(s => s.MutedUntilUtc = muted ? null : DateTimeOffset.UtcNow.AddHours(1));
                break;

            case IdQuit:
                Post(() => _app.Quit());
                break;
        }
    }

    /// <summary>
    /// Menu commands arrive on the window's thread, which is the UI thread, but the tray callback
    /// can also run re-entrantly while a menu is up. Posting keeps window work off that stack.
    /// </summary>
    private void Post(Action action)
    {
        if (!_app.Dispatcher.TryEnqueue(() => action()))
            Debug.WriteLine("Chargle: could not reach the UI thread.");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            var data = CreateData();
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_icon != 0) { DestroyIcon(_icon); _icon = 0; }
        if (_hwnd != 0) { DestroyWindow(_hwnd); _hwnd = 0; }
        if (_classAtom != 0) UnregisterClass("ChargleTrayWindow", GetModuleHandle(null));
    }

    // ------------------------------------------------------------------ interop

    private const int NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const int NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04;
    private const int ImageIcon = 1, LrLoadFromFile = 0x0010;
    private const int SmCxSmIcon = 49, SmCySmIcon = 50;
    private const uint MfString = 0x0000, MfSeparator = 0x0800, MfChecked = 0x0008;
    private const uint TpmRightButton = 0x0002, TpmRightAlign = 0x0008, TpmBottomAlign = 0x0020;

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(
        nint instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(nint menu, uint flags, nint id, string? item);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool TrackPopupMenuEx(
        nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);
}
