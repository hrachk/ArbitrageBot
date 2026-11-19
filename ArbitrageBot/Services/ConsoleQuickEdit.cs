using System.Runtime.InteropServices;

namespace ArbitrageBot.Services;

/// <summary>
/// Turns off QuickEdit mode of the Windows console. With QuickEdit on, a single click in the
/// window starts a text selection that suspends console writes; every thread that logs then
/// blocks, so scans, orders and the UI feed all stall until the user presses Enter or Esc.
/// No-op on other platforms or when there is no console (service, redirected output).
/// </summary>
public static class ConsoleQuickEdit
{
    private const int StdInputHandle = -10;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    public static void Disable()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = GetStdHandle(StdInputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
            if (!GetConsoleMode(handle, out var mode)) return;
            SetConsoleMode(handle, (mode & ~EnableQuickEditMode) | EnableExtendedFlags);
        }
        catch
        {
            // Not fatal: the bot runs the same, only the console can be paused by a click.
        }
    }
}
