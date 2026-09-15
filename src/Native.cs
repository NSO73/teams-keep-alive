// The whole P/Invoke surface.

using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TeamsKeepAlive
{
  internal static class Native
  {
    // ---- keystroke

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
      internal int dx, dy;
      internal uint mouseData, dwFlags, time;
      internal IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
      internal ushort wVk, wScan;
      internal uint dwFlags, time;
      internal IntPtr dwExtraInfo;
    }

    // mi is never used, but as the largest arm it gives INPUT the size SendInput checks.
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
      [FieldOffset(0)] internal MOUSEINPUT mi;
      [FieldOffset(0)] internal KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
      internal uint type;
      internal INPUTUNION u;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_F15 = 0x7E;
    private const int Signature = 0x544B4131;   // "TKA1" for input loggers

    private static readonly INPUT[] PhantomKey = BuildPhantomKey();

    private static INPUT[] BuildPhantomKey()
    {
      var inputs = new INPUT[2];
      for (int i = 0; i < inputs.Length; i++)
      {
        inputs[i].type = INPUT_KEYBOARD;
        inputs[i].u.ki.wVk = VK_F15;
        inputs[i].u.ki.dwExtraInfo = new IntPtr(Signature);
      }
      inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
      return inputs;
    }

    // F15 is on almost no keyboard, so hardly anything reacts to it, yet the idle clock resets.
    // False when refused, as UIPI does under an elevated foreground window.
    internal static bool SendPhantomKey() =>
      SendInput((uint)PhantomKey.Length, PhantomKey, Marshal.SizeOf<INPUT>()) == PhantomKey.Length;

    // ---- clocks

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
      internal uint cbSize;
      internal uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("kernel32.dll")]
    internal static extern ulong GetTickCount64();

    internal const int WM_TIMECHANGE = 0x001E;   // broadcast when the clock or time zone changes

    // The session's last input on the tick clock: the idle time Teams reads.
    internal static uint? LastInputTick()
    {
      var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
      return GetLastInputInfo(ref info) ? info.dwTime : (uint?)null;
    }

    // ---- wake-up timer

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint elapseMs, IntPtr timerProc);

    [DllImport("user32.dll")]
    internal static extern bool KillTimer(IntPtr hwnd, IntPtr id);

    internal const int WM_TIMER = 0x0113;

    // ---- executable's own icon

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr module, IntPtr name, uint type, int cx, int cy,
                                           uint load);

    private const uint IMAGE_ICON = 1;
    private const uint LR_SHARED = 0x8000;
    private const int AppIconResourceId = 32512;   // the id csc gives /win32icon

    // LR_SHARED: the system owns the handle and Icon.FromHandle takes no ownership, so there is
    // nothing to dispose. A failed load throws.
    internal static Icon LoadAppIcon(Size size) =>
      Icon.FromHandle(LoadImage(GetModuleHandle(null), new IntPtr(AppIconResourceId), IMAGE_ICON,
                                size.Width, size.Height, LR_SHARED));

    // ---- menu chrome

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value,
                                                    int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    // The dark flag sets the colour of the hairline DWM draws around the rounded popup.
    internal static void RoundCorners(IntPtr hwnd, bool dark)
    {
      int corner = DWMWCP_ROUND, darkMode = dark ? 1 : 0;
      DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
      DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
    }

    // ---- session state

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll")]
    internal static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId,
                                                          int infoClass, out IntPtr buffer,
                                                          out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    internal const int WM_WTSSESSION_CHANGE = 0x02B1;
    internal const int WTS_CONSOLE_CONNECT = 0x1;
    internal const int WTS_CONSOLE_DISCONNECT = 0x2;
    internal const int WTS_REMOTE_CONNECT = 0x3;
    internal const int WTS_REMOTE_DISCONNECT = 0x4;
    internal const int WTS_SESSION_LOCK = 0x7;
    internal const int WTS_SESSION_UNLOCK = 0x8;
    internal const int NOTIFY_FOR_THIS_SESSION = 0;
    private const int WTS_CURRENT_SESSION = -1;
    private const int WTSSessionInfoEx = 25;
    private const int WTSDisconnected = 4;
    private const int WTS_SESSIONSTATE_LOCK = 0;

    // WTSINFOEXW: a DWORD level, then a union its LARGE_INTEGERs align on 8 bytes in either
    // bitness. Held by checks, though only the live one tells the flags from the state before them.
    private const int SessionIdOffset = 8;
    private const int SessionStateOffset = 12;
    private const int SessionFlagsOffset = 16;

    // The session's own lock flag. Not a refused input desktop: a UAC prompt refuses it too, and no
    // unlock notice comes to undo the verdict.
    internal static bool SessionIsLocked() =>
      TryReadSession(out _, out _, out int flags) && flags == WTS_SESSIONSTATE_LOCK;

    // No client attached: a remote session closed without signing out, or the console handed over
    // to another user.
    internal static bool SessionIsDisconnected() =>
      TryReadSession(out _, out int state, out _) && state == WTSDisconnected;

    // False when unreadable, which reads as unlocked and connected: a false "locked" or
    // "disconnected" would stop the run, a false "unlocked" or "connected" only counts misses until
    // the next notice.
    internal static bool TryReadSession(out int sessionId, out int state, out int flags)
    {
      sessionId = state = flags = -1;
      if (!WTSQuerySessionInformation(IntPtr.Zero, WTS_CURRENT_SESSION, WTSSessionInfoEx,
                                      out IntPtr buffer, out int bytes)) return false;
      try
      {
        if (bytes < SessionFlagsOffset + sizeof(int) || Marshal.ReadInt32(buffer) != 1)
          return false;
        sessionId = Marshal.ReadInt32(buffer, SessionIdOffset);
        state = Marshal.ReadInt32(buffer, SessionStateOffset);
        flags = Marshal.ReadInt32(buffer, SessionFlagsOffset);
        return true;
      }
      finally
      {
        WTSFreeMemory(buffer);
      }
    }

    // ---- second launch

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string caption);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    internal static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    internal static readonly int ShowMenuMessage = RegisterWindowMessage(Program.Id + ".ShowMenu");

    // A failed registration leaves 0, which is WM_NULL: other programs send that to check a window
    // still responds, and each check would open the menu.
    internal static bool IsShowMenu(int msg) => msg != 0 && msg == ShowMenuMessage;

    // Posted, not sent: a wedged daemon cannot hang this process. The daemon has no visible window
    // to take the foreground with: without this right, a click elsewhere would not close its menu.
    internal static void ShowDaemonMenu()
    {
      IntPtr hwnd = FindWindow(null, Program.DaemonName);
      if (hwnd == IntPtr.Zero) return;
      GetWindowThreadProcessId(hwnd, out int owner);
      AllowSetForegroundWindow(owner);
      PostMessage(hwnd, ShowMenuMessage, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- diagnostics

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern void OutputDebugString(string message);

    // To an attached debugger or DebugView. Not Trace: csc compiles its calls out without
    // /define:TRACE.
    internal static void Log(object message) => OutputDebugString(Program.Id + ": " + message);
  }
}
