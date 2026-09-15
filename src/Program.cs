using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle(TeamsKeepAlive.Strings.AppName)]
[assembly: AssemblyProduct(TeamsKeepAlive.Strings.AppName)]
[assembly: AssemblyCompany("NSO73")]
[assembly: AssemblyCopyright("Copyright (c) 2026 NSO73 - WTFPL v2")]
// AssemblyVersion comes from build.cmd. This one MSBuild writes and a bare csc does not: without
// it the runtime takes the executable for a .NET Framework 4.0 one, and keeps every WinForms and
// System.Drawing fix since then off, PNG icon frames among them.
[assembly: TargetFramework(".NETFramework,Version=v4.8")]
// P/Invoke DLLs load from System32 alone. Neither wtsapi32.dll nor dwmapi.dll is a KnownDLL or
// loaded before its first call, so the default search would take a copy planted beside the
// executable first, in a folder its user can write to.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace TeamsKeepAlive
{
  internal static class Program
  {
    // The prefix of every name the tool gives Windows: mutex, window, message, logon task and
    // registry key.
    internal const string Id = "TeamsKeepAlive";

    // Given to both the mutex and the message window of the running copy, which a second launch
    // looks for.
    internal const string DaemonName = Id + ".Daemon";

    // Passed by the logon task, so a start at logon tells itself from a launch by hand.
    internal const string LogonSwitch = "/logon";

    [STAThread]
    private static void Main(string[] args)
    {
      using (OpenDaemonMutex(out bool owner))
      {
        // Launched by hand, a second copy opens the first one's menu; at logon it adds nothing.
        if (owner) Run();
        else if (Array.IndexOf(args, LogonSwitch) < 0) Native.ShowDaemonMenu();
      }
    }

    // Local\: one daemon per session, no privilege needed. Null when a copy running under another
    // account holds it, as one started through a UAC credential prompt: its mutex grants this
    // account no access, but it is another copy all the same.
    private static Mutex OpenDaemonMutex(out bool owner)
    {
      try
      {
        return new Mutex(true, @"Local\" + DaemonName, out owner);
      }
      catch (UnauthorizedAccessException)
      {
        owner = false;
        return null;
      }
    }

    // Out of Main, so a second copy loads no WinForms: the JIT loads what a method names.
    private static void Run()
    {
      Application.EnableVisualStyles();
      var app = new TrayApp();
      // No WinForms exception dialog: a tray daemon has nowhere to show it. Before Start, whose
      // message window can already call back.
      Application.ThreadException += (sender, e) => app.OnFault(e.Exception);
      try
      {
        app.Start();
      }
      catch (Exception e)
      {
        // The one dialog: without the icon, a failed start would leave no trace.
        app.Dispose();
        MessageBox.Show(Strings.StartFailed + Environment.NewLine + e.Message, Strings.AppName,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
        return;
      }
      Application.Run(app);   // disposes app once it exits
    }
  }
}
