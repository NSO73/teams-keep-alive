using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TeamsKeepAlive
{
  // Everything the daemon reads or does outside itself: Windows in TrayApp, a fake in Tests.cs.
  internal interface IHost
  {
    DateTime Now();          // the local wall clock
    ulong Tick();            // GetTickCount64: never set, sleep included; low 32 bits = idle clock
    uint? LastInputTick();   // null when unreadable
    bool SendKey();          // false when refused
    void WakeIn(int ms);
    void StopWaking();
    void Save(bool enabled, AutoStopRule autoStop);
    void Show();             // reads Look, Status, Note, Active and AutoStop
  }

  internal enum TrayLook { Active, Paused, Blocked }

  // The session notices that decide whether a keystroke can land.
  internal enum SessionChange { Lock, Unlock, Disconnect, Connect }

  // After a number of hours, at a time of day, or never (the default value): never both.
  internal readonly struct AutoStopRule
  {
    internal static readonly int[] OfferedHours = { 0, 1, 4, 8 };   // the menu's rows; 0 is Never
    internal static readonly AutoStopRule Never = default;

    internal readonly int AfterHours;   // 0 unless a duration
    internal readonly int? AtMinutes;   // null unless a time of day

    private AutoStopRule(int afterHours, int? atMinutes) =>
      (AfterHours, AtMinutes) = (afterHours, atMinutes);

    // Anything the menu has no row for is Never, so a stored rule always has a row to tick.
    internal static AutoStopRule After(int hours) =>
      Array.IndexOf(OfferedHours, hours) >= 0 ? new AutoStopRule(hours, null) : Never;

    internal static AutoStopRule At(int minutes) =>
      minutes >= 0 && minutes < TimeOfDay.DayMinutes ? new AutoStopRule(0, minutes) : Never;
  }

  // The run: when the keystroke goes out, when the run stops, what the tray shows. UI thread only.
  internal sealed class Daemon
  {
    internal const int IntervalMs = 60000;     // well under the Teams idle threshold (~5 min)
    internal const int MinimumWakeMs = 250;    // so an overdue wake-up cannot become a busy loop
    // UIPI refuses injection while an elevated window has the foreground, an ordinary few minutes
    // at the keyboard, so a single miss is not believed.
    internal const int MissesBeforeBlocked = 5;
    private const int LandingSlackMs = 2000;   // the idle clock's granularity
    private const int HourMs = 60 * 60 * 1000;

    private readonly IHost host;
    private bool enabled;          // persisted: a deadline pauses without clearing it
    private bool pausedByDeadline;
    private bool locked;
    private bool disconnected;     // no client attached: a remote session closed, a user switch
    private bool faulted;
    private ulong? stopTick;       // a duration's end, on the tick clock: blind to clock changes
    private DateTime? stopLocal;   // a time of day's, on the wall clock it follows
    private uint? lastSentTick;
    private bool lastAccepted;     // so the next round judges whether it landed
    private int misses;

    internal Daemon(IHost host, bool enabled, AutoStopRule autoStop)
    {
      this.host = host;
      this.enabled = enabled;
      AutoStop = autoStop;
    }

    internal bool Active => enabled && !pausedByDeadline;
    internal AutoStopRule AutoStop { get; private set; }   // persisted

    internal TrayLook Look =>
      faulted || Blocked ? TrayLook.Blocked : Active ? TrayLook.Active : TrayLook.Paused;

    // The tooltip's second line.
    internal string Status =>
      faulted ? Strings.StatusFault
      : !Active ? Strings.StatusPaused
      : Blocked ? Strings.StatusBlocked
      : StopTime() is DateTime stop ? Strings.StatusUntil(stop) : Strings.StatusActive;

    // Beside Active in the menu.
    internal string Note =>
      faulted ? Strings.MenuFault
      : Blocked ? Strings.MenuBlocked
      : StopTime() is DateTime stop ? Strings.MenuUntil(stop) : "";

    // Misses are cleared on every stop, so blocked implies active.
    private bool Blocked => misses >= MissesBeforeBlocked;

    private bool Running => Active && !locked && !disconnected;

    internal void Start()
    {
      ArmStop();
      Refresh();
    }

    internal void ToggleActive()
    {
      enabled = !Active;
      pausedByDeadline = false;
      Commit();
    }

    // A new rule lifts a pause by deadline, not one asked for by hand.
    internal void SetAutoStop(AutoStopRule rule)
    {
      AutoStop = rule;
      pausedByDeadline = false;
      Commit();
    }

    // KillTimer leaves a WM_TIMER already queued, so one can still arrive once stopped.
    internal void OnWake()
    {
      if (Running) Round();
    }

    // Behind the lock screen or with no client attached nothing lands, so the run stops. Each is
    // lifted by its own notice alone, whatever the order: a session can be reconnected still
    // locked. Resuming runs a round: an auto-stop may have fallen due meanwhile. At start the
    // session counts as unlocked and connected until told otherwise.
    internal void OnSessionChange(SessionChange change)
    {
      if (change == SessionChange.Lock || change == SessionChange.Unlock)
        locked = change == SessionChange.Lock;
      else
        disconnected = change == SessionChange.Disconnect;
      if (Running) Round(); else Refresh();
    }

    // The wall clock or the time zone changed. A stop at a time of day is never left later than
    // that time's next occurrence, as a clock set back a day would leave it; one the clock went
    // past falls due at the next round.
    internal void OnTimeChange()
    {
      if (stopLocal is DateTime stop && AutoStop.AtMinutes is int at)
      {
        DateTime next = TimeOfDay.Next(at, host.Now());
        if (next < stop) stopLocal = next;
      }
      Refresh();
    }

    // The icon stays red until restart. Refreshing re-arms a wake-up the fault may have cut short.
    internal void OnFault()
    {
      faulted = true;
      Refresh();
    }

    // Shows the state and sleeps until the next thing due. Stopped, the run forgets its keystrokes,
    // so a resumed run starts with no evidence against it.
    internal void Refresh()
    {
      if (!Running)
      {
        lastSentTick = null;
        lastAccepted = false;
        misses = 0;
      }
      host.Show();   // first: a wake-up that throws must not leave the tray stale
      // Read again: Show calls the shell, whose wait can dispatch a lock notice.
      if (Running) host.WakeIn(NextWakeMs()); else host.StopWaking();
    }

    // Since the later of the last input and the last keystroke: the keystroke floor stops a retry
    // loop when injection is refused, and an unreadable idle clock never reads as just active.
    // Unsigned, so the 49.7-day wrap changes nothing.
    private static uint SinceActivityMs(uint? lastInput, uint? lastSent, uint now) =>
      Math.Min(now - lastInput ?? uint.MaxValue, now - lastSent ?? uint.MaxValue);

    // Saved last, so a failed save surfaces as a fault over a state already applied.
    private void Commit()
    {
      ArmStop();
      Refresh();
      host.Save(enabled, AutoStop);
    }

    private void Round()
    {
      if (UntilStopMs() <= 0)
      {
        pausedByDeadline = true;   // not saved: nothing persisted changes
        ArmStop();
      }
      else
      {
        // Input first: an input between the two reads would otherwise wrap into 49.7 days idle.
        uint? lastInput = host.LastInputTick();
        uint now = (uint)host.Tick();
        if (SinceActivityMs(lastInput, lastSentTick, now) >= IntervalMs)
        {
          // Each keystroke counts once: a refused one as it goes out, an accepted one here, a
          // round later, where one that landed clears the count. A keystroke that lands resets
          // the idle clock: idle longer than the time since it went out means it went nowhere.
          // False with nothing to compare.
          bool missed = now - lastInput > now - lastSentTick + LandingSlackMs;
          if (lastAccepted) misses = missed ? misses + 1 : 0;
          lastAccepted = host.SendKey();
          if (!lastAccepted) misses++;
          lastSentTick = now;
        }
      }
      Refresh();
    }

    // The next keystroke or the auto-stop, whichever comes first.
    private int NextWakeMs()
    {
      uint? lastInput = host.LastInputTick();
      double ms = IntervalMs - (double)SinceActivityMs(lastInput, lastSentTick, (uint)host.Tick());
      if (UntilStopMs() is double untilStop) ms = Math.Min(ms, untilStop);
      return (int)Math.Max(MinimumWakeMs, Math.Ceiling(ms));
    }

    // On every start, resume and new rule, so a resume never trips over an instant gone by.
    private void ArmStop()
    {
      stopTick = Active && AutoStop.AfterHours > 0
                 ? host.Tick() + (ulong)AutoStop.AfterHours * HourMs : (ulong?)null;
      stopLocal = Active && AutoStop.AtMinutes is int at
                  ? TimeOfDay.Next(at, host.Now()) : (DateTime?)null;
    }

    // Until the run stops, on the clock its rule follows; null without an auto-stop.
    private double? UntilStopMs() =>
      stopTick is ulong end ? (long)end - (long)host.Tick()
      : (stopLocal - host.Now())?.TotalMilliseconds;

    // When the run stops, for display only. A duration's end goes through UTC, or one crossing a
    // daylight saving change would show an hour off; compared with Now, the hour repeated in
    // autumn would stop the run early.
    private DateTime? StopTime() =>
      stopTick is ulong end
      ? host.Now().ToUniversalTime().AddMilliseconds((long)end - (long)host.Tick()).ToLocalTime()
      : stopLocal;
  }

  // A time of day as minutes since midnight.
  internal static class TimeOfDay
  {
    internal const int DayMinutes = 24 * 60;

    // 18:30, 18h30, 18 h 30, 18.30, 18,30, 1830, 930, or a bare 18. ASCII digits: int.Parse takes
    // no other.
    private static readonly Regex Pattern =
      new Regex(@"^\s*(?<h>[0-9]{1,2})(?:\s*[:hH.,]\s*(?<m>[0-9]{1,2})?|(?<m>[0-9]{2}))?\s*$");

    internal static bool TryParse(string text, out int minutes)
    {
      minutes = 0;
      Match match = Pattern.Match(text ?? "");
      if (!match.Success) return false;
      int h = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
      Group m = match.Groups["m"];
      int min = m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : 0;
      if (h > 23 || min > 59) return false;
      minutes = h * 60 + min;
      return true;
    }

    // 24-hour digits, which TryParse reads back.
    internal static string Format(int minutes) =>
      string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", minutes / 60, minutes % 60);

    // Later today when still ahead, tomorrow otherwise.
    internal static DateTime Next(int minutes, DateTime now)
    {
      DateTime today = now.Date.AddMinutes(minutes);
      return today > now ? today : today.AddDays(1);
    }

    // The first half hour at least 30 minutes out, seconds included: the time field's default.
    internal static int NextHalfHour(DateTime now)
    {
      const long HalfHourTicks = TimeSpan.TicksPerMinute * 30;
      long ticks = now.AddMinutes(30).TimeOfDay.Ticks;
      long halves = (ticks + HalfHourTicks - 1) / HalfHourTicks;   // rounded up
      return (int)(halves * 30 % DayMinutes);
    }
  }
}
