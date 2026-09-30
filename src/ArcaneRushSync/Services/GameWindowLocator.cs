using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ArcaneRushSync.Services;

public readonly record struct GameWindowBounds(
    nint Handle,
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsForeground);

public static class GameWindowLocator
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    public static bool TryGetBounds(out GameWindowBounds bounds)
    {
        bounds = default;
        var processes = Process.GetProcessesByName(AppConfig.GameProcessName);
        try
        {
            foreach (var process in processes.OrderBy(p => p.Id))
            {
                nint handle;
                try { handle = process.MainWindowHandle; }
                catch { continue; }

                if (handle == nint.Zero
                    || !IsWindowVisible(handle)
                    || IsIconic(handle)
                    || !GetWindowRect(handle, out var rect))
                    continue;

                var widthPx = rect.Right - rect.Left;
                var heightPx = rect.Bottom - rect.Top;
                if (widthPx < 320 || heightPx < 240)
                    continue;

                var dpi = GetDpiForWindow(handle);
                if (dpi == 0) dpi = 96;
                var scale = dpi / 96d;

                bounds = new GameWindowBounds(
                    handle,
                    rect.Left / scale,
                    rect.Top / scale,
                    widthPx / scale,
                    heightPx / scale,
                    GetForegroundWindow() == handle);
                return true;
            }

            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
}
