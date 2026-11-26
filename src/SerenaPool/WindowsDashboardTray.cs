using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SerenaPool;

[SupportedOSPlatform("windows")]
internal static partial class WindowsDashboardTray
{
    private const uint TrayMessage = 0x8000; // WM_APP
    private const uint StopMessage = TrayMessage + 1;
    private const ushort LeftButtonUp = 0x0202;
    private const ushort NinKeySelect = 0x0401;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const int SmallIconWidth = 49; // SM_CXSMICON
    private const int SmallIconHeight = 50; // SM_CYSMICON
    private const uint IconFormatVersion = 0x00030000;
    private const uint MessageIconTip = 0x87; // NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP

    public static void Start(Uri address, bool openOnLaunch, CancellationToken stopping)
    {
        var thread = new Thread(() =>
        {
            if (openOnLaunch && !stopping.IsCancellationRequested)
                WebDashboard.OpenBrowser(address);
            try { Run(address, stopping); }
            catch (Win32Exception error) { Console.Error.WriteLine($"无法启动 Serena Pool 托盘：{error.Message}"); }
        }) { IsBackground = true, Name = "Serena Pool Tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static unsafe void Run(Uri address, CancellationToken stopping)
    {
        if (stopping.IsCancellationRequested)
            return;

        // The window and its subclass belong to this message-loop thread.
        var window = CreateWindowEx(0, "STATIC", "Serena Pool", 0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (window == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        var context = GCHandle.Alloc(address);
        nint icon = 0;
        try
        {
            if (SetWindowSubclass(window, &OnMessage, 1, (nuint)GCHandle.ToIntPtr(context)) == 0)
                throw new Win32Exception("无法注册托盘窗口的消息处理程序。");

            icon = LoadProjectIcon();
            var data = new NotifyIconData
            {
                Size = (uint)sizeof(NotifyIconData), Window = window, Flags = MessageIconTip,
                Callback = TrayMessage, Icon = icon, Version = 4
            };
            $"Serena Pool · {address.Authority}".AsSpan().CopyTo(new Span<char>(data.Tip, 127));
            if (ShellNotifyIcon(NimAdd, ref data) == 0)
                throw new Win32Exception("Windows 通知区域没有接受托盘图标。");
            try
            {
                if (ShellNotifyIcon(NimSetVersion, ref data) == 0)
                    throw new Win32Exception("无法设置托盘图标的通知版本。");

                using var registration = stopping.Register(() => PostMessage(window, StopMessage, 0, 0));
                int status;
                while ((status = GetMessage(out var message, 0, 0, 0)) > 0)
                    DispatchMessage(ref message);
                if (status < 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            finally { ShellNotifyIcon(NimDelete, ref data); }
        }
        finally
        {
            if (icon != 0)
                DestroyIcon(icon);
            DestroyWindow(window);
            context.Free();
        }
    }

    private static unsafe nint LoadProjectIcon()
    {
        using var resource = typeof(WindowsDashboardTray).Assembly.GetManifestResourceStream("SerenaPool.Icon.Png")!;
        var bytes = new byte[resource.Length];
        resource.ReadExactly(bytes);
        int width = GetSystemMetrics(SmallIconWidth);
        int height = GetSystemMetrics(SmallIconHeight);
        fixed (byte* png = bytes)
        {
            var icon = CreateIconFromResourceEx(png, (uint)bytes.Length, 1, IconFormatVersion, width, height, 0);
            return icon != 0 ? icon : throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [UnmanagedCallersOnly]
    private static nint OnMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == StopMessage)
        {
            PostQuitMessage(0);
            return 0;
        }
        if (message == TrayMessage && (ushort)lParam is LeftButtonUp or NinKeySelect)
        {
            WebDashboard.OpenBrowser((Uri)GCHandle.FromIntPtr((nint)data).Target!);
            return 0;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Type;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    private static partial int ShellNotifyIcon(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial nint CreateIconFromResourceEx(byte* bits, uint length, int isIcon,
        uint version, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint window);

    [LibraryImport("comctl32.dll")]
    private static unsafe partial int SetWindowSubclass(nint window,
        delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessage(out Message message, nint window, uint first, uint last);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(ref Message message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    private static partial int PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);
}
