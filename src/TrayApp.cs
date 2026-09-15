using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TeamsKeepAlive
{
  // Windows around the Daemon: the tray icon, the menu, the message window, the logon task and the
  // settings. Every decision about the run is the Daemon's.
  internal sealed class TrayApp : ApplicationContext, IHost
  {
    private const int SchedulerTimeoutMs = 10000;

    private readonly Settings settings = new Settings(Program.Id);
    private MessageWindow window;
    private Icon activeIcon, pausedIcon, blockedIcon;
    private TrayMenu menu;
    private NotifyIcon tray;
    private AutoStartAgent autoStartAgent;
    private Daemon daemon;
    private bool autoStart, autoStartFailed;

    internal void Start()
    {
      // First, so a second launch finds it early: its request is posted and waits for the loop.
      window = new MessageWindow(onWake: () => daemon.OnWake(), onTimeChange: OnTimeChange,
                                 onSessionChange: change => daemon.OnSessionChange(change),
                                 onShowMenu: ShowMenuAtCursor);

      // A failed load throws, so Program.Run reports it rather than leave an empty tray slot.
      Size size = SystemInformation.SmallIconSize;
      activeIcon = Native.LoadAppIcon(size);   // the executable's own: nothing to dispose
      pausedIcon = EmbeddedIcon("paused.ico", size);
      blockedIcon = EmbeddedIcon("blocked.ico", size);

      menu = new TrayMenu(toggleActive: () => daemon.ToggleActive(),
                          setAutoStop: rule => daemon.SetAutoStop(rule),
                          toggleAutoStart: () => autoStartAgent.Set(!autoStart), quit: ExitThread);
      tray = new NotifyIcon { ContextMenuStrip = menu.Strip };
      // Left only: NotifyIcon raises a double-click for every button, so a hurried second
      // right-click would pause the run.
      tray.MouseDoubleClick += (sender, e) =>
      {
        if (e.Button == MouseButtons.Left) daemon.ToggleActive();
      };
      // Not disposed: the context wraps the thread's shared marshalling control. Location, since
      // Application.ExecutablePath goes through a URI and reads a folder named R%26D as R&D.
      autoStartAgent = new AutoStartAgent(new WindowsFormsSynchronizationContext(),
                                          new LogonTask(Program.Id),
                                          Assembly.GetExecutingAssembly().Location,
                                          OnAutoStartAnswer, () => Task.Delay(SchedulerTimeoutMs));

      (bool enabled, AutoStopRule autoStop) = settings.Load();
      daemon = new Daemon(this, enabled, autoStop);
      daemon.Start();   // sets the icon: a NotifyIcon without one shows nothing
      tray.Visible = true;

      // Once all is built: see MessageWindow. Refused, locks and disconnects would go unseen: a
      // fault, and the run goes on. The session is read after, so no change falls between the two.
      if (!window.ListenForSessionChanges()) OnFault(new Win32Exception());
      if (Native.SessionIsLocked()) daemon.OnSessionChange(SessionChange.Lock);
      if (Native.SessionIsDisconnected()) daemon.OnSessionChange(SessionChange.Disconnect);

      // Off the UI thread, its answer posted for the loop. Last, so no Start that throws leaves
      // that answer to land on a disposed tray while the start error shows.
      autoStartAgent.Sync();
    }

    // A fault: the details go to the debugger output, the daemon turns the icon red. As the
    // Application.ThreadException handler, it lets nothing escape: that would end the process.
    internal void OnFault(Exception e)
    {
      Native.Log(e);
      try
      {
        daemon?.OnFault();
      }
      catch (Exception again)
      {
        Native.Log(again);
      }
    }

    // .NET caches the time zone for the process's life. A broadcast, which Windows can deliver
    // inside any call of Start that waits on another thread or process: the daemon may not exist.
    private void OnTimeChange()
    {
      TimeZoneInfo.ClearCachedData();
      daemon?.OnTimeChange();
    }

    // Null: nothing was learned, so the tick stays, with a note beside it. A click that asked has
    // already closed the menu, so a notification tells it too; not the startup Sync, which a slow
    // logon can outwait.
    private void OnAutoStartAnswer(bool? enabled, bool asked)
    {
      autoStart = enabled ?? autoStart;
      autoStartFailed = enabled == null;
      Show();
      if (autoStartFailed && asked)
        tray.ShowBalloonTip(0, Strings.AutoStartFailedTitle, Strings.AutoStartFailed,
                            ToolTipIcon.Warning);
    }

    // Takes the foreground right Native.ShowDaemonMenu handed over.
    private void ShowMenuAtCursor()
    {
      Native.SetForegroundWindow(window.Handle);
      menu.Strip.Show(Cursor.Position);
    }

    private static Icon EmbeddedIcon(string name, Size size)
    {
      using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        return new Icon(stream, size);
    }

    // ---- IHost

    public DateTime Now() => DateTime.Now;
    public ulong Tick() => Native.GetTickCount64();
    public uint? LastInputTick() => Native.LastInputTick();
    public bool SendKey() => Native.SendPhantomKey();
    public void WakeIn(int ms) => window.WakeIn(ms);
    public void StopWaking() => window.StopWaking();
    public void Save(bool enabled, AutoStopRule autoStop) => settings.Save(enabled, autoStop);

    // WinForms ignores an icon, a text or a tick set to what it already shows.
    public void Show()
    {
      TrayLook look = daemon.Look;
      tray.Icon = look == TrayLook.Active ? activeIcon
                  : look == TrayLook.Paused ? pausedIcon : blockedIcon;
      tray.Text = Strings.Tooltip(daemon.Status);
      menu.Mirror(daemon.Active, daemon.AutoStop, autoStart, daemon.Note,
                  autoStartFailed ? Strings.MenuAutoStartFailed : "");
    }

    // ---- shutdown

    // Also after a Start that threw part way. Destroying the window kills its timer; disposing the
    // NotifyIcon removes it from the tray.
    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        window?.Dispose();
        tray?.Dispose();
        menu?.Dispose();
        pausedIcon?.Dispose();
        blockedIcon?.Dispose();
      }
      base.Dispose(disposing);
    }
  }

  // The invisible window that the wake-up timer, clock changes, session notices and a second
  // launch's request arrive on. Top-level, so broadcasts reach it. Owned by the UI thread, so all
  // land there with no marshalling and no SystemEvents subscription.
  internal sealed class MessageWindow : NativeWindow, IDisposable
  {
    private static readonly IntPtr WakeTimer = new IntPtr(1);

    private readonly Action onWake, onTimeChange, onShowMenu;
    private readonly Action<SessionChange> onSessionChange;

    internal MessageWindow(Action onWake, Action onTimeChange,
                           Action<SessionChange> onSessionChange, Action onShowMenu)
    {
      this.onWake = onWake;
      this.onTimeChange = onTimeChange;
      this.onSessionChange = onSessionChange;
      this.onShowMenu = onShowMenu;
      // No style bits: never shown, never in Alt+Tab.
      CreateHandle(new CreateParams { Caption = Program.DaemonName });
    }

    // At the end of TrayApp.Start: a notice is sent, and a sent message is dispatched inside any
    // blocking call, so it must not reach a half-built TrayApp. False when refused.
    internal bool ListenForSessionChanges() =>
      Native.WTSRegisterSessionNotification(Handle, Native.NOTIFY_FOR_THIS_SESSION);

    // Re-armed in place, where a WinForms Timer destroys its window on every stop. A failure
    // throws: a timer that never fires would leave a green icon over a run that does nothing.
    internal void WakeIn(int ms)
    {
      if (Native.SetTimer(Handle, WakeTimer, (uint)ms, IntPtr.Zero) == IntPtr.Zero)
        throw new Win32Exception();
    }

    internal void StopWaking() => Native.KillTimer(Handle, WakeTimer);

    // Unregistering a window that never registered is harmless.
    public void Dispose()
    {
      Native.WTSUnRegisterSessionNotification(Handle);
      DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
      if (m.Msg == Native.WM_TIMER && m.WParam == WakeTimer) onWake();
      else if (m.Msg == Native.WM_TIMECHANGE) onTimeChange();
      else if (m.Msg == Native.WM_WTSSESSION_CHANGE)
      {
        if (ChangeOf(m.WParam.ToInt32()) is SessionChange change) onSessionChange(change);
      }
      else if (Native.IsShowMenu(m.Msg)) onShowMenu();
      base.WndProc(ref m);
    }

    // NativeWindow drops what WndProc throws unless told where to send it: the fault handler.
    protected override void OnThreadException(Exception e) => Application.OnThreadException(e);

    // Null for the notices that change nothing for a keystroke, a logon among them.
    private static SessionChange? ChangeOf(int reason)
    {
      switch (reason)
      {
        case Native.WTS_SESSION_LOCK: return SessionChange.Lock;
        case Native.WTS_SESSION_UNLOCK: return SessionChange.Unlock;
        case Native.WTS_CONSOLE_DISCONNECT:
        case Native.WTS_REMOTE_DISCONNECT: return SessionChange.Disconnect;
        case Native.WTS_CONSOLE_CONNECT:
        case Native.WTS_REMOTE_CONNECT: return SessionChange.Connect;
        default: return null;
      }
    }
  }

  // A key under HKCU\Software: the tool's name, or a throwaway one in the live checks. A value the
  // menu has no row for reads as Never; a hand edit that stores both halves gets the time of day.
  internal sealed class Settings
  {
    private readonly string keyPath;

    internal Settings(string name) => keyPath = @"Software\" + name;

    internal (bool Enabled, AutoStopRule AutoStop) Load()
    {
      using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath))
      {
        int Read(string name, int fallback) => key?.GetValue(name) is int value ? value : fallback;
        AutoStopRule atTime = AutoStopRule.At(Read("AutoStopAt", -1));
        return (Read("Enabled", 1) != 0,
                atTime.AtMinutes.HasValue ? atTime : AutoStopRule.After(Read("AutoStopHours", 0)));
      }
    }

    internal void Save(bool enabled, AutoStopRule autoStop)
    {
      using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
      {
        key.SetValue("Enabled", enabled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AutoStopHours", autoStop.AfterHours, RegistryValueKind.DWord);
        key.SetValue("AutoStopAt", autoStop.AtMinutes ?? -1, RegistryValueKind.DWord);
      }
    }
  }
}
