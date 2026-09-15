// Every word the tool shows, in English or French from the Windows display language. Both live
// here: a satellite assembly would break the single-file build.

using System;
using System.Globalization;

namespace TeamsKeepAlive
{
  internal static class Strings
  {
    internal const string AppName = "Teams Keep-Alive";   // not translated

    // First: static initialisers run in declaration order.
    private static readonly bool Fr =
      CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr";

    private static string S(string en, string fr) => Fr ? fr : en;

    // The ampersand marks the access key; Tests.cs keeps them distinct within each menu level.
    internal static readonly string MenuActive = S("&Active", "&Actif");
    internal static readonly string MenuAutoStop = S("A&uto-stop", "A&rrêt auto");
    internal static readonly string MenuAtTime = S("At a &set time", "À une heure &précise");
    internal static readonly string MenuAutoStart =
      S("Start with &Windows", "Démarrer avec &Windows");
    internal static readonly string MenuQuit = S("&Quit", "&Quitter");
    internal static readonly string MenuBlocked = S("blocked", "bloqué");
    internal static readonly string MenuFault = S("error", "erreur");
    internal static readonly string MenuAutoStartFailed = S("failed", "échec");

    // The time field's name for screen readers: it has no caption of its own.
    internal static readonly string TimeFieldName = MenuAtTime.Replace("&", "");

    internal static readonly string StatusActive = S("Active", "Actif");
    internal static readonly string StatusPaused = S("Paused", "En pause");
    internal static readonly string StatusBlocked = S("Keystroke blocked", "Frappe bloquée");
    internal static readonly string StatusFault = S("Internal error", "Erreur interne");

    internal static readonly string StartFailed = S("Could not start:", "Démarrage impossible :");

    // After a Start with Windows click the scheduler refused or left unanswered. NotifyIcon passes
    // on 63 characters of title and 255 of text: Tests.cs holds both to it.
    internal static readonly string AutoStartFailedTitle = MenuAutoStart.Replace("&", "");
    internal static readonly string AutoStartFailed =
      S("The Task Scheduler refused the change or did not answer in time.",
        "Le Planificateur de tâches a refusé la modification ou n'a pas répondu à temps.");

    // 0 is the Never row. No access key on a duration: a time typed over the open drop-down would
    // click the row its first digit names.
    internal static string MenuDuration(int hours) =>
      hours > 0 ? string.Format(S("In {0} h", "Dans {0} h"), hours) : S("&Never", "&Jamais");

    internal static string MenuUntil(DateTime time) =>
      string.Format(S("until {0}", "jusqu'à {0}"), Clock(time));

    internal static string StatusUntil(DateTime time) =>
      string.Format(S("Active until {0}", "Actif jusqu'à {0}"), Clock(time));

    // On two lines. NotifyIcon refuses a text over 63 characters: Tests.cs holds every state to it.
    internal static string Tooltip(string status) => AppName + "\n" + status;

    // Invariant: a culture's own time separator would replace the colon.
    private static string Clock(DateTime time) =>
      time.ToString("HH:mm", CultureInfo.InvariantCulture);
  }
}
