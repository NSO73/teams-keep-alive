// Checks for what can go wrong unnoticed. Compiled into the program's assembly, so internals are
// reachable. No side effects, and every clock the daemon reads is a fake. With live, checks
// against the Task Scheduler, the registry and this session too, under throwaway names, from a
// prompt without elevation, so they hold for a standard account.
//
//   build.cmd test [live]

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;
using TeamsKeepAlive;

internal static class Tests
{
  private const string ThisCopy = @"C:\Tools\TeamsKeepAlive.exe";
  private const string ThisUser = "S-1-5-21-1-2-3-1001";

  // SystemInformation.SmallIconSize at 100, 125, 150 and 200 % scaling.
  private static readonly int[] TraySizes = { 16, 20, 24, 32 };

  // What TimeOfDay.TryParse reads each text as, -1 when it refuses it.
  private static readonly (string Text, int Minutes)[] TimeTexts =
  {
    ("18:30", 1110), ("18h30", 1110), ("18H30", 1110), ("18.30", 1110), ("18,30", 1110),
    ("18 h 30", 1110), ("1830", 1110), ("930", 570), ("0700", 420), ("18", 1080), ("0", 0),
    ("23:59", 1439), (" 7:05 ", 425), ("7: 5", 425), ("18:", 1080),
    ("24:00", -1), ("18:60", -1), ("-1", -1), ("99", -1), ("abc", -1), ("", -1), (null, -1),
    ("12:ab", -1), (":30", -1), ("12345", -1), ("１８:30", -1)
  };

  private static int failures, total;

  // Strings settles its language at type load, so nothing touches it before the culture is set.
  private static int Main(string[] args)
  {
    if (args.Length > 0)
      Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(args[0]);
    Console.WriteLine("-- " + CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    TimeOfDayChecks();
    KeystrokeChecks();
    StopChecks();
    StringChecks();
    MenuChecks();
    WindowsChecks();
    AutoStartChecks();
    if (Array.IndexOf(args, "live") >= 0) LiveChecks();

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? $"{total} checks passed"
                      : $"{failures} of {total} checks FAILED");
    return failures == 0 ? 0 : 1;
  }

  private static void Check(string what, bool ok)
  {
    total++;
    if (!ok) failures++;
    Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
  }

  // ---- time of day and rules

  private static void TimeOfDayChecks()
  {
    Console.WriteLine("TimeOfDay.TryParse (-1: refused)");
    foreach ((string text, int expected) in TimeTexts)
      Check($"'{text ?? "<null>"}' -> {expected}",
            TimeOfDay.TryParse(text, out int got) ? got == expected : expected < 0);
    Check("Format and TryParse round-trip every minute of the day", RoundTrips());

    Console.WriteLine("TimeOfDay.Next, NextHalfHour");
    var nine = new DateTime(2026, 6, 15, 9, 0, 0);
    Check("later today when still ahead", TimeOfDay.Next(18 * 60, nine) == nine.AddHours(9));
    Check("tomorrow once gone by, this very minute included",
          TimeOfDay.Next(9 * 60, nine) == nine.AddDays(1));
    Check("a minute before midnight is still today",
          TimeOfDay.Next(1439, nine.AddHours(14).AddMinutes(58))
          == nine.AddHours(14).AddMinutes(59));
    Check("the default rounds up to the half hour past 30 minutes out",
          TimeOfDay.NextHalfHour(nine.AddMinutes(50)) == 10 * 60 + 30);
    Check("exactly 30 minutes out on a half hour", TimeOfDay.NextHalfHour(nine) == 9 * 60 + 30);
    Check("a second more is past it", TimeOfDay.NextHalfHour(nine.AddSeconds(1)) == 10 * 60);
    Check("and wraps to 00:00, never 24:00",
          TimeOfDay.NextHalfHour(nine.AddHours(14).AddMinutes(15)) == 0);

    Console.WriteLine("AutoStopRule");
    Check("the default value is Never, not midnight", IsNever(default(AutoStopRule)));
    Check("After keeps exactly the durations the menu offers", AfterKeepsOfferedDurations());
    Check("At keeps the whole day and nothing outside it",
          AutoStopRule.At(0).AtMinutes == 0
          && AutoStopRule.At(TimeOfDay.DayMinutes - 1).AtMinutes == TimeOfDay.DayMinutes - 1
          && IsNever(AutoStopRule.At(-1)) && IsNever(AutoStopRule.At(TimeOfDay.DayMinutes)));
  }

  private static bool RoundTrips()
  {
    for (int m = 0; m < TimeOfDay.DayMinutes; m++)
      if (!TimeOfDay.TryParse(TimeOfDay.Format(m), out int back) || back != m) return false;
    return true;
  }

  private static bool IsNever(AutoStopRule rule) =>
    rule.AfterHours == 0 && !rule.AtMinutes.HasValue;

  private static bool AfterKeepsOfferedDurations()
  {
    for (int hours = -48; hours <= 48; hours++)
    {
      bool offered = Array.IndexOf(AutoStopRule.OfferedHours, hours) >= 0;
      if (AutoStopRule.After(hours).AfterHours != (offered ? hours : 0)) return false;
    }
    return true;
  }

  // ---- daemon, on a fake host

  private static void KeystrokeChecks()
  {
    Console.WriteLine("Daemon, the keystroke");
    Check("idle at start: wakes at the floor, sends, then waits a full interval", IdleStartSends());
    Check("in use at start: waits out the interval and sends nothing", BusyStartWaits());
    Check("refused round after round: blocked from the threshold on, not before", RefusalsBlock());
    Check("a refusal counts once: the keystroke accepted next does not tip it over",
          RefusalCountsOnce());
    Check("accepted but going nowhere blocks too", SilentFailuresBlock());
    Check("landing again: active once the next round has judged one", LandingClears());
    Check("landing a second behind the keystroke still counts", LateLandingCounts());
    Check("a refused keystroke still waits an interval: no retry loop", RefusalWaits());
    Check("an unreadable idle clock reads as due, never as active", UnreadableIdleSends());
  }

  private static void StopChecks()
  {
    Console.WriteLine("Daemon, stops and resumes");
    Check("a pause by hand saves and stops the wake-ups; resuming saves and wakes",
          PauseByHandSaves());
    Check("a pause clears the evidence against the keystroke", PauseClearsMisses());
    Check("locked: no wake-up, no evidence, no keystroke from a stray WM_TIMER; unlocked: wakes",
          LockStops());
    Check("a lock handled inside the tray update leaves no wake-up armed", LockInsideShowStops());
    Check("a disconnect stops the run as a lock does, and each holds it until its own end",
          DisconnectStops());
    Check("a duration stops at its end to the second, without saving", DurationStops());
    Check("setting the clock neither stretches nor cuts a duration", DurationIgnoresClockSet());
    Check("a time of day follows the wall clock, and says until when", TimeOfDayFollowsClock());
    Check("the clock set back a day: a time of day stops at its next occurrence, not a day late",
          ClockSetBackRearms());
    Check("a nearer auto-stop brings the wake-up forward", StopBringsWakeForward());
    Check("resuming re-arms a time of day for tomorrow instead of stopping at once",
          ResumeRearms());
    Check("a new rule replaces the old one, down to Never", NewRuleReplacesOld());
    Check("a new rule lifts a pause by deadline, not one by hand", NewRuleLiftsDeadlinePause());
    Check("Active lifts a pause by deadline too, the auto-stop armed afresh",
          ToggleLiftsDeadlinePause());
    Check("unlocked after the auto-stop fell due: the round run at unlock pauses, sending nothing",
          UnlockStops());
    Check("a fault turns the icon red for good, over a pause too, and re-arms a wake-up",
          FaultShows());
    Check("a wake-up or a save that throws leaves the tray showing what was applied",
          FailuresStillShow());
  }

  private static bool IdleStartSends()
  {
    var host = new FakeHost(idleSeconds: 120);
    Daemon daemon = Started(host);
    if (host.WakeMs != Daemon.MinimumWakeMs || host.Sent != 0) return false;
    host.Pass(1);
    daemon.OnWake();
    return host.Sent == 1 && host.WakeMs == Daemon.IntervalMs && daemon.Look == TrayLook.Active;
  }

  private static bool BusyStartWaits()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    if (host.WakeMs != Daemon.IntervalMs) return false;
    host.Pass(30);
    host.IdleFor(10);
    daemon.OnWake();
    return host.Sent == 0 && host.WakeMs == Daemon.IntervalMs - 10000;
  }

  private static bool RefusalsBlock()
  {
    Daemon early = Refused(new FakeHost(), Daemon.MissesBeforeBlocked - 1);
    Daemon late = Refused(new FakeHost(), Daemon.MissesBeforeBlocked);
    return early.Look == TrayLook.Active && late.Look == TrayLook.Blocked
           && late.Status == Strings.StatusBlocked && late.Note == Strings.MenuBlocked;
  }

  // One refusal short of the threshold, then accepted: the last refusal is not judged again.
  private static bool RefusalCountsOnce()
  {
    var host = new FakeHost();
    Daemon daemon = Refused(host, Daemon.MissesBeforeBlocked - 1);
    host.Accepts = true;
    Round(host, daemon);
    return daemon.Look == TrayLook.Active;
  }

  // The first keystroke has nothing to be judged against, hence the extra round.
  private static bool SilentFailuresBlock()
  {
    var host = new FakeHost(idleSeconds: 120) { Lands = false };
    Daemon daemon = Started(host);
    for (int i = 0; i < Daemon.MissesBeforeBlocked; i++) Round(host, daemon);
    bool early = daemon.Look == TrayLook.Active;
    Round(host, daemon);
    return early && daemon.Look == TrayLook.Blocked;
  }

  // A keystroke is judged as the next goes out, so the first that lands clears nothing yet.
  private static bool LandingClears()
  {
    var host = new FakeHost();
    Daemon daemon = Refused(host, Daemon.MissesBeforeBlocked);
    host.Accepts = true;
    Round(host, daemon);
    bool stillBlocked = daemon.Look == TrayLook.Blocked;
    Round(host, daemon);
    return stillBlocked && daemon.Look == TrayLook.Active;
  }

  // The idle clock can stamp a keystroke behind the tick it went out on: its granularity.
  private static bool LateLandingCounts()
  {
    var host = new FakeHost(idleSeconds: 120) { LandsBehindMs = 1000 };
    Daemon daemon = Started(host);
    for (int i = 0; i <= Daemon.MissesBeforeBlocked; i++) Round(host, daemon);
    return daemon.Look == TrayLook.Active;
  }

  private static bool RefusalWaits()
  {
    var host = new FakeHost();
    Refused(host, 1);
    return host.Sent == 1 && host.WakeMs == Daemon.IntervalMs;
  }

  private static bool UnreadableIdleSends()
  {
    var host = new FakeHost { LastInput = null, Lands = false };
    Daemon daemon = Started(host);
    host.Pass(1);
    daemon.OnWake();
    return host.Sent == 1 && host.WakeMs == Daemon.IntervalMs && daemon.Look == TrayLook.Active;
  }

  private static bool PauseByHandSaves()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    daemon.ToggleActive();
    bool paused = host.Saved == 1 && host.WakeMs == null && daemon.Look == TrayLook.Paused
                  && daemon.Status == Strings.StatusPaused && daemon.Note == "";
    daemon.ToggleActive();
    return paused && host.Saved == 2 && host.WakeMs != null && daemon.Look == TrayLook.Active;
  }

  private static bool PauseClearsMisses()
  {
    Daemon daemon = Refused(new FakeHost(), Daemon.MissesBeforeBlocked);
    daemon.ToggleActive();
    daemon.ToggleActive();
    return daemon.Look == TrayLook.Active;
  }

  // Locked from the start too: TrayApp reports a session found locked the same way.
  private static bool LockStops()
  {
    var host = new FakeHost();
    Daemon daemon = Refused(host, Daemon.MissesBeforeBlocked);
    daemon.OnSessionChange(SessionChange.Lock);
    if (host.WakeMs != null || daemon.Look != TrayLook.Active) return false;
    int sent = host.Sent;
    Round(host, daemon);   // a WM_TIMER queued before the kill
    if (host.Sent != sent || host.WakeMs != null) return false;
    daemon.OnSessionChange(SessionChange.Unlock);
    return host.WakeMs == Daemon.IntervalMs;
  }

  // Updating the tray calls the shell, whose wait can dispatch a lock notice before Show returns.
  private static bool LockInsideShowStops()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    host.DuringShow = () => daemon.OnSessionChange(SessionChange.Lock);
    daemon.Refresh();
    return host.WakeMs == null;
  }

  // Reconnected behind the lock screen, the run waits for the unlock; unlocked while still
  // disconnected, it waits for the reconnect.
  private static bool DisconnectStops()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    daemon.OnSessionChange(SessionChange.Disconnect);
    Round(host, daemon);   // a WM_TIMER queued before the kill
    if (host.WakeMs != null || host.Sent != 0) return false;
    daemon.OnSessionChange(SessionChange.Lock);
    daemon.OnSessionChange(SessionChange.Connect);
    bool lockHolds = host.WakeMs == null;
    daemon.OnSessionChange(SessionChange.Unlock);
    bool resumed = host.WakeMs != null;
    daemon.OnSessionChange(SessionChange.Disconnect);
    daemon.OnSessionChange(SessionChange.Lock);
    daemon.OnSessionChange(SessionChange.Unlock);
    bool disconnectHolds = host.WakeMs == null;
    daemon.OnSessionChange(SessionChange.Connect);
    return lockHolds && resumed && disconnectHolds && host.WakeMs != null;
  }

  private static bool DurationStops()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    daemon.SetAutoStop(AutoStopRule.After(1));   // the rule itself is saved
    host.Pass(3600 - 1);
    WakeInUse(host, daemon);
    bool early = daemon.Active;
    host.Pass(1);
    WakeInUse(host, daemon);
    return early && !daemon.Active && daemon.Look == TrayLook.Paused && host.Saved == 1
           && host.WakeMs == null;
  }

  // Ten minutes into an hour, the wall clock is set an hour ahead: the stop is still fifty minutes
  // away, shown from the new wall-clock time.
  private static bool DurationIgnoresClockSet()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    daemon.SetAutoStop(AutoStopRule.After(1));
    host.Pass(600);
    host.Wall = host.Wall.AddHours(1);
    daemon.OnTimeChange();
    bool shown = daemon.Note == Strings.MenuUntil(host.Wall.AddMinutes(50));
    host.Pass(50 * 60 - 1);
    WakeInUse(host, daemon);
    bool early = daemon.Active;
    host.Pass(1);
    WakeInUse(host, daemon);
    return shown && early && !daemon.Active;
  }

  // A stop at 18:00 set at noon: the wall clock set to 18:00 stops the run at once, six hours
  // before the tick clock gets there.
  private static bool TimeOfDayFollowsClock()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    daemon.SetAutoStop(AutoStopRule.At(18 * 60));
    DateTime six = host.Wall.Date.AddHours(18);
    bool shown = daemon.Note == Strings.MenuUntil(six) && daemon.Status == Strings.StatusUntil(six);
    host.Wall = six;
    daemon.OnTimeChange();
    WakeInUse(host, daemon);
    return shown && !daemon.Active;
  }

  // The same stop, then the clock set back to noon the day before: the run stops at 18:00 on the
  // day the clock now shows, not a day later.
  private static bool ClockSetBackRearms()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    daemon.SetAutoStop(AutoStopRule.At(18 * 60));
    host.Wall = host.Wall.AddDays(-1);
    daemon.OnTimeChange();
    host.Pass(6 * 3600 - 1);
    WakeInUse(host, daemon);
    bool early = daemon.Active;
    host.Pass(1);
    WakeInUse(host, daemon);
    return early && !daemon.Active;
  }

  private static bool StopBringsWakeForward()
  {
    var host = new FakeHost { Wall = new DateTime(2026, 6, 15, 11, 59, 55) };
    Daemon daemon = Started(host);
    daemon.SetAutoStop(AutoStopRule.At(12 * 60));
    return host.WakeMs == 5000;
  }

  // Set for 12:30 at noon, paused by hand, resumed at 13:00: the stop moves to tomorrow.
  private static bool ResumeRearms()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    daemon.SetAutoStop(AutoStopRule.At(12 * 60 + 30));
    daemon.ToggleActive();
    host.Pass(3600);
    daemon.ToggleActive();
    WakeInUse(host, daemon);
    DateTime tomorrow = host.Wall.Date.AddDays(1).AddMinutes(12 * 60 + 30);
    return daemon.Active && daemon.Note == Strings.MenuUntil(tomorrow);
  }

  private static bool NewRuleReplacesOld()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    daemon.SetAutoStop(AutoStopRule.After(1));
    daemon.SetAutoStop(AutoStopRule.At(18 * 60));
    bool atTime = daemon.Note == Strings.MenuUntil(host.Wall.Date.AddHours(18));
    daemon.SetAutoStop(AutoStopRule.Never);
    return atTime && daemon.Note == "" && daemon.Status == Strings.StatusActive;
  }

  private static bool NewRuleLiftsDeadlinePause()
  {
    var host = new FakeHost();
    Daemon byDeadline = Started(host);
    byDeadline.SetAutoStop(AutoStopRule.After(1));
    host.Pass(3600);
    WakeInUse(host, byDeadline);
    if (byDeadline.Active) return false;
    byDeadline.SetAutoStop(AutoStopRule.After(4));

    Daemon byHand = Started(new FakeHost());
    byHand.ToggleActive();
    byHand.SetAutoStop(AutoStopRule.After(4));
    return byDeadline.Active && !byHand.Active && byHand.Note == "";
  }

  private static bool ToggleLiftsDeadlinePause()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    daemon.SetAutoStop(AutoStopRule.After(1));
    host.Pass(3600);
    WakeInUse(host, daemon);
    if (daemon.Active) return false;
    daemon.ToggleActive();
    return daemon.Active && daemon.Note == Strings.MenuUntil(host.Wall.AddHours(1));
  }

  // Idle all along, so a keystroke sent before the deadline is judged would show.
  private static bool UnlockStops()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    daemon.SetAutoStop(AutoStopRule.After(1));
    daemon.OnSessionChange(SessionChange.Lock);
    host.Pass(2 * 3600);
    daemon.OnSessionChange(SessionChange.Unlock);
    return !daemon.Active && host.WakeMs == null && host.Sent == 0;
  }

  private static bool FaultShows()
  {
    Daemon paused = Started(new FakeHost());
    paused.ToggleActive();
    paused.OnFault();
    bool red = paused.Look == TrayLook.Blocked && paused.Status == Strings.StatusFault
               && paused.Note == Strings.MenuFault;
    paused.ToggleActive();
    red &= paused.Look == TrayLook.Blocked;

    var host = new FakeHost();
    Daemon running = Started(host);
    host.WakeMs = null;   // as if the fault had cut it short
    running.OnFault();
    return red && host.WakeMs == Daemon.IntervalMs;
  }

  // SetTimer or the registry refusing: the tray already shows what was applied.
  private static bool FailuresStillShow()
  {
    var host = new FakeHost();
    Daemon daemon = Started(host);
    host.Fails = true;
    int shown = host.Shown;
    bool red = Throws(daemon.OnFault) && host.Shown == shown + 1 && daemon.Look == TrayLook.Blocked;
    return red && Throws(daemon.ToggleActive) && host.Shown == shown + 2 && host.WakeMs == null;
  }

  // Stands in for Windows under the daemon: clocks the check moves, a keystroke taken and landing
  // or not, and a record of what the daemon asked for.
  private sealed class FakeHost : IHost
  {
    internal DateTime Wall = new DateTime(2026, 6, 15, 12, 0, 0);
    internal ulong Ticks = (1UL << 32) - 150000;   // most checks cross the 32-bit wrap
    internal uint? LastInput;                      // the last real input
    internal bool Accepts = true;                  // SendInput takes the keystroke...
    internal bool Lands = true;                    // ...and it resets the idle clock...
    internal int LandsBehindMs;                    // ...stamped this far behind
    internal bool Fails;                           // WakeIn and Save throw
    internal int? WakeMs;                          // the wake-up armed, null when none
    internal Action DuringShow;                    // runs inside the next Show, once
    internal int Sent, Saved, Shown;

    internal FakeHost(int idleSeconds = 0) => IdleFor(idleSeconds);

    internal void IdleFor(int seconds) => LastInput = (uint)Ticks - (uint)seconds * 1000;

    // Both clocks move alike; a check sets Wall alone to set the clock.
    internal void Pass(int seconds)
    {
      Wall = Wall.AddSeconds(seconds);
      Ticks += (ulong)seconds * 1000;
    }

    public DateTime Now() => Wall;
    public ulong Tick() => Ticks;
    public uint? LastInputTick() => LastInput;

    public bool SendKey()
    {
      Sent++;
      if (Accepts && Lands) LastInput = (uint)Ticks - (uint)LandsBehindMs;
      return Accepts;
    }

    public void WakeIn(int ms) => WakeMs = Fails ? throw new InvalidOperationException() : ms;
    public void StopWaking() => WakeMs = null;

    public void Save(bool enabled, AutoStopRule autoStop)
    {
      if (Fails) throw new InvalidOperationException();
      Saved++;
    }

    public void Show()
    {
      Shown++;
      Interlocked.Exchange(ref DuringShow, null)?.Invoke();
    }
  }

  private static Daemon Started(FakeHost host)
  {
    var daemon = new Daemon(host, true, AutoStopRule.Never);
    daemon.Start();
    return daemon;
  }

  // One interval passes, and the wake-up fires.
  private static void Round(FakeHost host, Daemon daemon)
  {
    host.Pass(Daemon.IntervalMs / 1000);
    daemon.OnWake();
  }

  // The keystroke refused round after round, the user away the whole time.
  private static Daemon Refused(FakeHost host, int rounds)
  {
    host.Accepts = false;
    host.IdleFor(120);
    Daemon daemon = Started(host);
    for (int i = 0; i < rounds; i++) Round(host, daemon);
    return daemon;
  }

  // In use: only an auto-stop can be due.
  private static void WakeInUse(FakeHost host, Daemon daemon)
  {
    host.IdleFor(0);
    daemon.OnWake();
  }

  private static bool Throws(Action act)
  {
    try
    {
      act();
      return false;
    }
    catch (InvalidOperationException)
    {
      return true;
    }
  }

  // ---- strings

  private static void StringChecks()
  {
    Console.WriteLine("Strings");
    Check("in the language asked for",
          Strings.StatusPaused == (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr"
                                   ? "En pause" : "Paused"));
    using (var menu = new TrayMenu(null, null, null, null))
      Check("access keys in the real menu: a letter each at most, none twice in a menu",
            MnemonicsAreDistinct(menu.Strip.Items));
    Check("times keep their colon whatever the culture's separator", ClockIgnoresCulture());
    foreach (string status in new[]
    {
      Strings.StatusActive, Strings.StatusPaused, Strings.StatusBlocked, Strings.StatusFault,
      Strings.StatusUntil(DateTime.Today)
    })
      Check($"'{status}' fits the 63 characters NotifyIcon takes",
            Strings.Tooltip(status).Length <= 63);
    Check("the Start with Windows notice fits NotifyIcon: 63 characters of title, 255 of text",
          Strings.AutoStartFailedTitle.Length <= 63 && Strings.AutoStartFailed.Length <= 255);
  }

  // At most one ampersand in an entry, before a letter, and no letter twice in a menu, drop-downs
  // included.
  private static bool MnemonicsAreDistinct(ToolStripItemCollection items)
  {
    string seen = "";
    foreach (ToolStripItem item in items)
    {
      if (!(item is ToolStripMenuItem row)) continue;
      if (row.HasDropDownItems && !MnemonicsAreDistinct(row.DropDownItems)) return false;
      string text = row.Text;
      int at = text.IndexOf('&');
      if (at < 0) continue;
      if (at == text.Length - 1 || !char.IsLetter(text[at + 1]) || text.IndexOf('&', at + 1) >= 0)
        return false;
      char key = char.ToLowerInvariant(text[at + 1]);
      if (seen.IndexOf(key) >= 0) return false;
      seen += key;
    }
    return true;
  }

  // A culture whose time separator is a dot, as Finnish has.
  private static bool ClockIgnoresCulture()
  {
    CultureInfo culture = Thread.CurrentThread.CurrentCulture;
    var dotted = (CultureInfo)CultureInfo.InvariantCulture.Clone();
    dotted.DateTimeFormat.TimeSeparator = ".";
    Thread.CurrentThread.CurrentCulture = dotted;
    try
    {
      return Strings.StatusUntil(new DateTime(2026, 6, 15, 18, 30, 0))
             .EndsWith("18:30", StringComparison.Ordinal);
    }
    finally
    {
      Thread.CurrentThread.CurrentCulture = culture;
    }
  }

  // ---- menu

  private static void MenuChecks()
  {
    Console.WriteLine("TrayMenu");
    Check("At a set time takes a click only while its field holds a time", AtTimeFollowsField());
    Check("the time field takes the menu's font and keeps its width", FieldKeepsFontAndWidth());
    Check("the time field has a name for screen readers", FieldHasAccessibleName());
    Check("the time field holds in full every time TryParse reads", FieldTakesEveryTime());
  }

  // Typing stops at MaxLength, and the part kept can read as another time: 18h 30 cut to five
  // characters applies 18:03.
  private static bool FieldTakesEveryTime()
  {
    using (var menu = new TrayMenu(null, null, null, null))
      foreach (ToolStripItem item in AllItems(menu.Strip.Items, new List<ToolStripItem>()))
        if (item is ToolStripTextBox field)
          return Array.TrueForAll(TimeTexts,
                                  t => t.Minutes < 0 || t.Text.Length <= field.MaxLength);
    return false;
  }

  // Left without the strip's font, a hosted control keeps the system menu font, and the row
  // joining the strip resets it to its default width.
  private static bool FieldKeepsFontAndWidth()
  {
    using (var menu = new TrayMenu(null, null, null, null))
    using (var stock = new ToolStripTextBox())
      foreach (ToolStripItem item in AllItems(menu.Strip.Items, new List<ToolStripItem>()))
        if (item is ToolStripTextBox field)
          return field.TextBox.Font.Equals(menu.Strip.Font) && field.Width != stock.Width;
    return false;
  }

  // Both ways a screen reader reaches it: walking the menu's items, and the focused edit control. A
  // name set on the control alone leaves the item's empty.
  private static bool FieldHasAccessibleName()
  {
    using (var menu = new TrayMenu(null, null, null, null))
      foreach (ToolStripItem item in AllItems(menu.Strip.Items, new List<ToolStripItem>()))
        if (item is ToolStripTextBox field)
          return field.AccessibilityObject.Name == Strings.TimeFieldName
                 && field.TextBox.AccessibilityObject.Name == Strings.TimeFieldName;
    return false;
  }

  // A click on the row closes the menu before it could say why it did nothing.
  private static bool AtTimeFollowsField()
  {
    using (var menu = new TrayMenu(null, null, null, null))
    {
      ToolStripTextBox field = null;
      ToolStripItem row = null;
      foreach (ToolStripItem item in AllItems(menu.Strip.Items, new List<ToolStripItem>()))
      {
        if (item is ToolStripTextBox box) field = box;
        else if (item.Text == Strings.MenuAtTime) row = item;
      }
      if (field == null || row == null) return false;
      field.Text = "abc";
      bool refused = !row.Enabled;
      field.Text = "18h30";
      return refused && row.Enabled;
    }
  }

  // Every item of a menu, drop-downs included, appended to all.
  private static List<ToolStripItem> AllItems(ToolStripItemCollection items,
                                              List<ToolStripItem> all)
  {
    foreach (ToolStripItem item in items)
    {
      all.Add(item);
      if (item is ToolStripMenuItem row && row.HasDropDownItems) AllItems(row.DropDownItems, all);
    }
    return all;
  }

  // ---- Windows

  private static void WindowsChecks()
  {
    Console.WriteLine("Windows");
    Check("the runtime takes the executable for a .NET Framework 4.8 one",
          AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName
          == ".NETFramework,Version=v4.8");
    Check("every P/Invoke is Native's and binds, its DLL taken from System32 alone",
          PInvokesBindFromSystem32());
    Check("the tick clock's low 32 bits are GetTickCount, the clock idle times count in",
          TickClocksAgree());
    Check("the executable's own icon resource loads", ActiveIconLoads());
    Check("active.ico: every tray size, green artwork",
          IconHolds("active.ico", c => c.G >= c.R, c => c.G - c.R > 60));
    Check("paused.ico: every tray size, neutral grey artwork",
          IconHolds("paused.ico", c => c.R == c.G && c.G == c.B, c => true));
    Check("blocked.ico: every tray size, red artwork",
          IconHolds("blocked.ico", c => c.R >= c.G && c.R >= c.B, c => c.R - c.G > 60));
    Check("the session query reads this process's own session, and a known state and lock flag",
          SessionQueryReadsOwnSession());
    Check("the wake-up timer fires through the message window",
          WindowSees(w => w.WakeIn(10), 10000) == "wake");
    Check("and stays silent once stopped",
          WindowSees(w => { w.WakeIn(10); w.StopWaking(); }, 300) == null);
    Check("a clock change reaches it", WindowSees(Post(Native.WM_TIMECHANGE), 10000) == "time");
    Check("so do the lock, unlock, disconnect and connect notices, each as what it is",
          SessionNotice(Native.WTS_SESSION_LOCK) == "Lock"
          && SessionNotice(Native.WTS_SESSION_UNLOCK) == "Unlock"
          && SessionNotice(Native.WTS_CONSOLE_DISCONNECT) == "Disconnect"
          && SessionNotice(Native.WTS_REMOTE_DISCONNECT) == "Disconnect"
          && SessionNotice(Native.WTS_CONSOLE_CONNECT) == "Connect"
          && SessionNotice(Native.WTS_REMOTE_CONNECT) == "Connect");
    const int WTS_SESSION_LOGON = 0x5;
    Check("but no other session notice, a logon among them",
          SessionNotice(WTS_SESSION_LOGON, 300) == null);
    Check("so does the show-menu message",
          WindowSees(Post(Native.ShowMenuMessage), 10000) == "menu");
    Check("and what a callback throws reaches the thread's fault handler",
          WindowSees(w => w.WakeIn(10), 10000, wakeThrows: true) == "fault");
  }

  // A DLL planted beside the executable never loads in place of the system's. Prelink binds each
  // import without calling it, so one misspelt, or missing from System32, fails here rather than
  // on a path seldom taken. The whole assembly is searched: an import outside Native fails too.
  private static bool PInvokesBindFromSystem32()
  {
    Assembly assembly = typeof(Native).Assembly;
    DefaultDllImportSearchPathsAttribute search =
      assembly.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
    const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    var imports = new List<MethodInfo>();
    foreach (Type type in assembly.GetTypes())
      foreach (MethodInfo method in type.GetMethods(Statics))
        if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0) imports.Add(method);
    try
    {
      imports.ForEach(Marshal.Prelink);
    }
    catch (TypeLoadException)   // DllNotFoundException, EntryPointNotFoundException
    {
      return false;
    }
    return search?.Paths == DllImportSearchPath.System32 && imports.Count > 0
           && imports.TrueForAll(m => m.DeclaringType == typeof(Native));
  }

  // The daemon measures idle time on the 64-bit clock's low half against GetLastInputInfo's.
  private static bool TickClocksAgree()
  {
    for (int i = 0; i < 100000; i++)
    {
      uint before = (uint)Environment.TickCount;
      uint low = (uint)Native.GetTickCount64();
      uint after = (uint)Environment.TickCount;
      if (low - before > after - before) return false;
    }
    return true;
  }

  // Pins the resource id csc gives /win32icon, which the build would not catch.
  private static bool ActiveIconLoads()
  {
    try
    {
      return Native.LoadAppIcon(new Size(16, 16)).Width == 16;
    }
    catch (ArgumentException)
    {
      return false;
    }
  }

  // Sizes come from the icon directory: LoadImage would scale a missing frame and report the size
  // asked for.
  private static bool IconHolds(string name, Func<Color, bool> every, Func<Color, bool> some)
  {
    foreach (int px in TraySizes)
      using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
      {
        if (stream == null) return false;
        using (var icon = new Icon(stream, new Size(px, px)))
        using (Bitmap bitmap = icon.ToBitmap())
        {
          if (icon.Width != px || icon.Height != px) return false;
          bool seen = false;
          for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
              Color c = bitmap.GetPixel(x, y);
              if (c.A == 0) continue;
              if (!every(c)) return false;
              seen |= some(c);
            }
          if (!seen) return false;
        }
      }
    return true;
  }

  // Pins the offsets read out of WTSINFOEXW: a misplaced read would take a live session for a
  // locked or disconnected one and silently stop the run. In session 0, as on a service-hosted
  // runner, the id cannot be told from the padding before it; both ranges still hold.
  private static bool SessionQueryReadsOwnSession()
  {
    using (Process self = Process.GetCurrentProcess())
      return Native.TryReadSession(out int id, out int state, out int flags)
             && id == self.SessionId && state >= 0 && state <= 9 && flags >= -1 && flags <= 1;
  }

  // On an STA thread of its own, like the UI thread: a message window whose callbacks, and the
  // thread's fault handler, name what reached them; act drives it, and messages are pumped until
  // something lands or the wait runs out. Null when nothing came.
  private static string WindowSees(Action<MessageWindow> act, int waitMs, bool wakeThrows = false)
  {
    string seen = null;
    var thread = new Thread(() =>
    {
      string Fail() => throw new InvalidOperationException();
      Application.ThreadException += delegate { seen = "fault"; };
      var window = new MessageWindow(onWake: () => seen = wakeThrows ? Fail() : "wake",
                                     onTimeChange: () => seen = "time",
                                     onSessionChange: change => seen = change.ToString(),
                                     onShowMenu: () => seen = "menu");
      try
      {
        act(window);
        Stopwatch waited = Stopwatch.StartNew();
        while (seen == null && waited.ElapsedMilliseconds < waitMs)
        {
          Application.DoEvents();
          Thread.Sleep(5);
        }
      }
      finally
      {
        window.Dispose();
      }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return seen;
  }

  private static Action<MessageWindow> Post(int msg, int wparam = 0) =>
    window => Native.PostMessage(window.Handle, msg, new IntPtr(wparam), IntPtr.Zero);

  private static string SessionNotice(int reason, int waitMs = 10000) =>
    WindowSees(Post(Native.WM_WTSSESSION_CHANGE, reason), waitMs);

  // ---- start with Windows

  private static void AutoStartChecks()
  {
    Console.WriteLine("LogonTask.TaskXml");
    Check("well formed, even with an ampersand in the path and markup in the account",
          TaskXmlParses(ThisUser, ThisCopy) && TaskXmlParses(ThisUser, @"C:\R&D\a.exe")
          && TaskXmlParses("<AD>\\a\"b\"", @"C:\a.exe"));
    Check("still no password and no elevation", TaskXmlStaysUnprivileged());
    Check("never held back or stopped by the scheduler: on battery, or after 72 hours",
          TaskXmlNeverStops());
    Check("carries the template's revision where the scheduler reads it",
          TaskXmlReader(ThisUser, ThisCopy)("//t:RegistrationInfo/t:Version")
          == LogonTask.TemplateRevision);
    Check("the template is the text its revision was pinned with", TemplateMatchesRevision());

    Console.WriteLine("AutoStartAgent.Sync");
    Check("no task: unticked", AfterSync(new FakeLogonTask(TaskState.None)) == "False / read");
    Check("a task switched off: unticked, left alone",
          AfterSync(new FakeLogonTask(TaskState.Off)) == "False / read");
    Check("a task that starts an existing copy: ticked, left to it",
          AfterSync(new FakeLogonTask(TaskState.On)) == "True / read");
    Check("a task whose copy is gone: repointed at this one",
          AfterSync(new FakeLogonTask(TaskState.Stale)) == $"True / read, register {ThisCopy}");
    Check("a task that starts this copy from another template: registered afresh",
          AfterSync(new FakeLogonTask(TaskState.Outdated)) == $"True / read, register {ThisCopy}");
    Check("a scheduler that refuses: nothing learned",
          AfterSync(new FakeLogonTask(TaskState.On) { Refuses = true }) == "null / read");

    Console.WriteLine("AutoStartAgent.Set");
    Check("ticking registers this copy",
          AfterSet(new FakeLogonTask(TaskState.None), true) == $"True / register {ThisCopy}");
    Check("unticking deletes the task",
          AfterSet(new FakeLogonTask(TaskState.On), false) == "False / delete");
    Check("refused: nothing learned, so the tick stays",
          AfterSet(new FakeLogonTask(TaskState.None) { Refuses = true }, true)
          == $"null / register {ThisCopy}");
    Check("changes asked during the startup read wait for it, and only the last one counts",
          ChangesDuringSyncCollapse());
    Check("only a click's answer counts as asked for, never the startup read's", OnlyClicksAsk());
    Check("a call the scheduler never answers is given up, and the next request runs",
          HungCallIsAbandoned());
  }

  private static bool TaskXmlParses(string user, string exePath)
  {
    try
    {
      Func<string, string> text = TaskXmlReader(user, exePath);
      return text("//t:Actions/t:Exec/t:Command") == exePath
             && text("//t:Actions/t:Exec/t:Arguments") == Program.LogonSwitch
             && text("//t:Principals/t:Principal/t:UserId") == user
             && text("//t:Triggers/t:LogonTrigger/t:UserId") == user;
    }
    catch (XmlException)
    {
      return false;
    }
  }

  private static bool TaskXmlStaysUnprivileged()
  {
    Func<string, string> text = TaskXmlReader(ThisUser, ThisCopy);
    return text("//t:Principal/t:LogonType") == "InteractiveToken"
           && text("//t:Principal/t:RunLevel") == "LeastPrivilege";
  }

  // Each default would end the daemon, or never start it, with nothing to show for it: a laptop
  // unplugged, a session left open for three days.
  private static bool TaskXmlNeverStops()
  {
    Func<string, string> text = TaskXmlReader(ThisUser, ThisCopy);
    return text("//t:Settings/t:DisallowStartIfOnBatteries") == "false"
           && text("//t:Settings/t:StopIfGoingOnBatteries") == "false"
           && text("//t:Settings/t:ExecutionTimeLimit") == "PT0S";
  }

  // Sync registers afresh only a task of another revision, so a template edited under the same one
  // never reaches the tasks already registered. On an edit, raise LogonTask.TemplateRevision, then
  // pin both here anew. Line ends follow the checkout, so they are normalised first.
  private static bool TemplateMatchesRevision()
  {
    const string PinnedRevision = "2";
    const string PinnedDigest = "GuxFpHaOQ8lJE2/F+Dqy5cjCLC2WTafAtjGvzQVAwGs=";   // SHA-256
    string xml = LogonTask.TaskXml(ThisUser, ThisCopy, "").Replace("\r\n", "\n");
    using (SHA256 sha = SHA256.Create())
    {
      string digest = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(xml)));
      return LogonTask.TemplateRevision == PinnedRevision && digest == PinnedDigest;
    }
  }

  // The task document for these values, read by XPath with t: for its namespace.
  private static Func<string, string> TaskXmlReader(string user, string exePath)
  {
    var doc = new XmlDocument();
    doc.LoadXml(LogonTask.TaskXml(user, exePath, LogonTask.TemplateRevision));
    var ns = new XmlNamespaceManager(doc.NameTable);
    ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
    return path => doc.SelectSingleNode(path, ns)?.InnerText;
  }

  // What the agent answers, then the calls it made.
  private static string AfterSync(FakeLogonTask task) =>
    Answers(task, agent => agent.Sync(), 1) + " / " + task.Log;

  private static string AfterSet(FakeLogonTask task, bool enabled) =>
    Answers(task, agent => agent.Set(enabled), 1) + " / " + task.Log;

  // Two clicks while the startup read is in flight: only the last one reaches the scheduler.
  private static bool ChangesDuringSyncCollapse()
  {
    var task = new FakeLogonTask(TaskState.On);
    string answers = Answers(task, agent =>
    {
      agent.Sync();
      agent.Set(false);
      agent.Set(true);
    }, 2);
    return answers == "True, True" && task.Log == $"read, register {ThisCopy}";
  }

  // The startup read, then a click while it runs: the click's answer alone says it was asked for,
  // so a slow logon that outwaits the read raises no notification.
  private static bool OnlyClicksAsk()
  {
    var home = new HeldContext();
    var asked = new List<bool>();
    var agent = new AutoStartAgent(home, new FakeLogonTask(TaskState.None), ThisCopy,
                                   (answer, byClick) => asked.Add(byClick), NeverTimesOut);
    agent.Sync();
    agent.Set(true);
    return home.DeliverNext(10000) && home.DeliverNext(10000)
           && string.Join(", ", asked) == "False, True";
  }

  // A scheduler that never answers: the call is given up once its wait runs out, the next request
  // runs meanwhile, and the abandoned call's late answer is never delivered. The check runs out
  // each wait itself; only the final silence is timed, and a slow machine can miss a late answer
  // there, never invent one.
  private static bool HungCallIsAbandoned()
  {
    var release = new ManualResetEvent(false);
    var task = new FakeLogonTask(TaskState.None) { Hold = release };
    var home = new HeldContext();
    var answers = new List<string>();
    var waits = new List<TaskCompletionSource<object>>();
    var agent = new AutoStartAgent(home, task, ThisCopy,
                                   (answer, asked) => answers.Add(Shown(answer)), () =>
    {
      var wait = new TaskCompletionSource<object>();
      waits.Add(wait);
      return wait.Task;
    });

    agent.Set(true);
    if (!SpinWait.SpinUntil(() => task.Log.Length > 0, 10000)) return false;   // hung in Register
    waits[0].SetResult(null);
    if (!home.DeliverNext(10000)) return false;   // given up
    agent.Set(false);
    if (!home.DeliverNext(10000)) return false;   // answered, not stuck behind the hang
    release.Set();
    waits[1].SetResult(null);
    if (home.DeliverNext(200)) return false;      // nothing more: the late answer is dropped
    return string.Join(", ", answers) == "null, False"
           && task.Log == $"register {ThisCopy}, delete";
  }

  // The answers delivered once act has asked, in order: exactly as many as expected, and none more
  // within a short silence. No scheduler wait ever runs out.
  private static string Answers(FakeLogonTask task, Action<AutoStartAgent> act, int expected)
  {
    var home = new HeldContext();
    var answers = new List<string>();
    var agent = new AutoStartAgent(home, task, ThisCopy,
                                   (answer, asked) => answers.Add(Shown(answer)), NeverTimesOut);
    act(agent);
    for (int i = 0; i < expected; i++)
      if (!home.DeliverNext(10000)) return "missing answer";
    if (home.DeliverNext(50)) return "extra answer";
    return string.Join(", ", answers);
  }

  private static string Shown(bool? answer) => answer?.ToString() ?? "null";

  private static Task NeverTimesOut() => new TaskCompletionSource<object>().Task;

  // Stands in for the scheduler, called from the agent's threads: answers from its state, logs
  // every call.
  private sealed class FakeLogonTask : ILogonTask
  {
    internal bool Refuses;
    internal ManualResetEvent Hold;   // set, the next call waits for it, like a hung scheduler
    private readonly List<string> calls = new List<string>();
    private TaskState state;

    internal FakeLogonTask(TaskState state) => this.state = state;

    internal string Log
    {
      get { lock (calls) return string.Join(", ", calls); }
    }

    public TaskState Read(string exePath)
    {
      Record("read");
      return state;
    }

    public void Register(string exePath)
    {
      Record("register " + exePath);
      state = TaskState.On;
    }

    public void Delete()
    {
      Record("delete");
      state = TaskState.None;
    }

    private void Record(string call)
    {
      lock (calls) calls.Add(call);
      Interlocked.Exchange(ref Hold, null)?.WaitOne();
      if (Refuses) throw new UnauthorizedAccessException();
    }
  }

  // Keeps what AutoStartAgent posts home until a check delivers it, one answer at a time.
  private sealed class HeldContext : SynchronizationContext
  {
    private readonly BlockingCollection<Action> posted = new BlockingCollection<Action>();

    public override void Post(SendOrPostCallback callback, object state) =>
      posted.Add(() => callback(state));

    // False when nothing comes within the wait.
    internal bool DeliverNext(int waitMs)
    {
      if (!posted.TryTake(out Action delivery, waitMs)) return false;
      delivery();
      return true;
    }
  }

  // ---- live: Windows itself

  // A throwaway task and a throwaway settings key, both removed at the end, and one keystroke into
  // this very session.
  private static void LiveChecks()
  {
    string name = Program.Id + ".Check";
    string taskName;
    bool administrator;
    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
    {
      taskName = name + "-" + identity.User.Value;
      administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    Console.WriteLine("Rights");
    Check("no administrator rights in force, so what follows holds for a standard account",
          !administrator);

    Console.WriteLine("LogonTask, against the Task Scheduler");
    var task = new LogonTask(name);
    // mscorlib: a file that exists but cannot start, so a run interrupted here leaves no task that
    // starts anything at logon. It stands for this copy, and ThisCopy for another.
    string existing = typeof(object).Assembly.Location;
    task.Delete();   // what an interrupted run left
    try
    {
      Check("none yet", task.Read(existing) == TaskState.None);
      task.Register(@"C:\" + Guid.NewGuid() + @"\TeamsKeepAlive.exe");
      Check("its program gone: stale", task.Read(existing) == TaskState.Stale);
      task.Register(existing);
      // On, not outdated: the revision reads back as written.
      Check("an existing file: on, under a name that ends with the user's SID",
            task.Read(existing) == TaskState.On && Schtasks($"/query /tn \"{taskName}\""));
      // As an older copy registered it, and a hand edit in the Task Scheduler may leave it. The
      // runtime lives under the Windows folder.
      string root = Environment.GetEnvironmentVariable("SystemRoot");
      new LogonTask(name, "1").Register("\"%SystemRoot%" + existing.Substring(root.Length) + "\"");
      Check("an older template, quoted, behind a variable: outdated for its copy, on for another",
            task.Read(existing) == TaskState.Outdated && task.Read(ThisCopy) == TaskState.On);
      Check("switched off in the Task Scheduler: off",
            Schtasks($"/change /tn \"{taskName}\" /disable")
            && task.Read(existing) == TaskState.Off);
      task.Register(existing);
      Check("ticked again over a task switched off: on", task.Read(existing) == TaskState.On);
    }
    finally
    {
      task.Delete();
    }
    task.Delete();
    Check("deleted, then deleted again: none", task.Read(existing) == TaskState.None);

    Console.WriteLine("Settings, against the registry");
    string keyPath = @"Software\" + name;
    Registry.CurrentUser.DeleteSubKey(keyPath, false);
    var settings = new Settings(name);
    Check("no key: active, never stopping", SettingsRead(settings, true, AutoStopRule.Never));
    try
    {
      Check("every kind of rule reads back as written",
            SettingsRoundTrip(settings, false, AutoStopRule.At(0))
            && SettingsRoundTrip(settings, true, AutoStopRule.At(TimeOfDay.DayMinutes - 1))
            && SettingsRoundTrip(settings, false, AutoStopRule.After(4))
            && SettingsRoundTrip(settings, true, AutoStopRule.Never));
      HandEdit(keyPath, ("AutoStopAt", 18 * 60), ("AutoStopHours", 4));
      Check("edited by hand with both halves: the time of day",
            SettingsRead(settings, true, AutoStopRule.At(18 * 60)));
      HandEdit(keyPath, ("Enabled", "0"), ("AutoStopAt", "600"), ("AutoStopHours", 3));
      Check("a value of another type, or an hour the menu has no row for: its default",
            SettingsRead(settings, true, AutoStopRule.Never));
    }
    finally
    {
      Registry.CurrentUser.DeleteSubKey(keyPath, false);
    }

    Console.WriteLine("This session");
    Check("at the keyboard, it reads as unlocked and connected",
          Native.TryReadSession(out _, out _, out _) && !Native.SessionIsLocked()
          && !Native.SessionIsDisconnected());
    uint? before = Native.LastInputTick();
    Check("the keystroke is accepted, and moves the idle clock",
          Native.SendPhantomKey()
          && SpinWait.SpinUntil(() => Native.LastInputTick() != before, 2000));
  }

  private static bool SettingsRead(Settings settings, bool enabled, AutoStopRule rule)
  {
    (bool enabledRead, AutoStopRule ruleRead) = settings.Load();
    return enabledRead == enabled && ruleRead.AfterHours == rule.AfterHours
           && ruleRead.AtMinutes == rule.AtMinutes;
  }

  private static bool SettingsRoundTrip(Settings settings, bool enabled, AutoStopRule rule)
  {
    settings.Save(enabled, rule);
    return SettingsRead(settings, enabled, rule);
  }

  // Replaces the key with these values, as a hand edit could leave it: an int goes in as a DWORD,
  // a string as REG_SZ.
  private static void HandEdit(string keyPath, params (string Name, object Value)[] values)
  {
    Registry.CurrentUser.DeleteSubKey(keyPath, false);
    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
      foreach ((string name, object value) in values) key.SetValue(name, value);
  }

  // Looks at a task from outside LogonTask. False when schtasks fails, which says why on stderr.
  private static bool Schtasks(string arguments)
  {
    var start = new ProcessStartInfo("schtasks.exe", arguments)
    {
      UseShellExecute = false, RedirectStandardOutput = true
    };
    using (Process process = Process.Start(start))
    {
      process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      return process.ExitCode == 0;
    }
  }
}
