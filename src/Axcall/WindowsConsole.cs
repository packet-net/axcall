using System.Runtime.InteropServices;

namespace Axcall;

/// <summary>
/// Windows-console tweaks for interactive use. The cmd/conhost default has
/// QuickEdit Mode enabled, which lets mouse clicks select and paste console
/// text straight into the input buffer — so clicking to focus the window can
/// inject on-screen text (e.g. the shell prompt) into stdin, which the relay
/// then transmits. Disabling QuickEdit Mode and mouse input stops that.
/// </summary>
internal static partial class WindowsConsole
{
    private const int StdInputHandle = -10;
    private const uint EnableMouseInput = 0x0010;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr handle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr handle, uint mode);

    /// <summary>
    /// Turn off QuickEdit Mode and mouse input on the console's stdin handle so
    /// mouse clicks can't paste console text into our input. No-op on non-Windows
    /// platforms and when stdin is redirected.
    /// </summary>
    public static void DisableQuickEditAndMouse()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected)
            return;

        var handle = GetStdHandle(StdInputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return;

        if (!GetConsoleMode(handle, out uint mode))
            return;

        mode &= ~(EnableQuickEditMode | EnableMouseInput);
        mode |= EnableExtendedFlags;
        SetConsoleMode(handle, mode);
    }
}
