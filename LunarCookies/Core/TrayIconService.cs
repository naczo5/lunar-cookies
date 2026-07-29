using System.Runtime.InteropServices;

namespace LunarCookies.Core;

public sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const int GwlpWndProc = -4;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    private readonly IntPtr _windowHandle;
    private readonly WindowProcedure _windowProcedure;
    private readonly IntPtr _previousWindowProcedure;
    private readonly IntPtr _iconHandle;
    private bool _added;
    private bool _disposed;

    public TrayIconService(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _windowProcedure = WndProc;
        _previousWindowProcedure = SetWindowLongPtr(
            windowHandle, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_windowProcedure));

        ExtractIconEx(Environment.ProcessPath!, 0, out IntPtr large, out IntPtr small, 1);
        _iconHandle = small != IntPtr.Zero ? small : large;
        if (small != IntPtr.Zero && large != IntPtr.Zero)
            DestroyIcon(large);
    }

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public void Show()
    {
        if (_disposed || _added)
            return;

        NotifyIconData data = CreateData(NifMessage | NifIcon | NifTip);
        _added = ShellNotifyIcon(NimAdd, ref data);
    }

    public void Hide()
    {
        if (!_added)
            return;
        NotifyIconData data = CreateData(0);
        ShellNotifyIcon(NimDelete, ref data);
        _added = false;
    }

    public void ShowBalloon(string title, string message)
    {
        Show();
        if (!_added)
            return;

        NotifyIconData data = CreateData(NifInfo);
        data.InfoTitle = title;
        data.Info = message;
        data.InfoFlags = NiifInfo;
        ShellNotifyIcon(NimModify, ref data);
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = 1,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        IconHandle = _iconHandle,
        Tip = "Lunar Cookies",
        Info = "",
        InfoTitle = ""
    };

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            uint notification = unchecked((uint)lParam.ToInt64());
            if (notification == WmLButtonDblClk)
            {
                OpenRequested?.Invoke();
                return IntPtr.Zero;
            }
            if (notification == WmRButtonUp)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }
        return CallWindowProc(_previousWindowProcedure, hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenu(menu, MfString, 1, "Open Lunar Cookies");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, 2, "Exit");
            GetCursorPos(out Point point);
            SetForegroundWindow(_windowHandle);
            uint command = TrackPopupMenu(
                menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, 0, _windowHandle, IntPtr.Zero);
            if (command == 1)
                OpenRequested?.Invoke();
            else if (command == 2)
                ExitRequested?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Hide();
        if (_previousWindowProcedure != IntPtr.Zero)
            SetWindowLongPtr(_windowHandle, GwlpWndProc, _previousWindowProcedure);
        if (_iconHandle != IntPtr.Zero)
            DestroyIcon(_iconHandle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    private static bool ShellNotifyIcon(uint message, ref NotifyIconData data) =>
        Shell_NotifyIcon(message, ref data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file, int iconIndex, out IntPtr largeIcon, out IntPtr smallIcon, uint iconCount);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(
        IntPtr previousWindowProcedure, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
