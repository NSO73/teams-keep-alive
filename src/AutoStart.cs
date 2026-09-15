// A per-user logon task rather than a Run key: no admin rights, and not cleared by the policies
// that wipe Run entries. LogonTask reaches it through the Task Scheduler's COM API, which blocks;
// AutoStartAgent runs it off the UI thread.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace TeamsKeepAlive
{
  // Off: disabled, or every trigger is. Stale: on, but the program it starts is gone. Outdated: on
  // and starting this very copy, but registered from another revision of the template.
  internal enum TaskState { None, Off, On, Stale, Outdated }

  // An interface, so AutoStartAgent's sequencing is checked without a scheduler. Each call throws
  // when the scheduler refuses.
  internal interface ILogonTask
  {
    TaskState Read(string exePath);   // this copy: only its own task can read as Outdated
    void Register(string exePath);    // creates or replaces: also repoints another copy's task
    void Delete();                    // done too when there was no task
  }

  internal sealed class LogonTask : ILogonTask
  {
    private const int TASK_CREATE_OR_UPDATE = 6;
    private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;
    private const int TASK_ACTION_EXEC = 0;
    private const int TaskNotFound = unchecked((int)0x80070002);   // ERROR_FILE_NOT_FOUND's HRESULT

    // Raised with every change to TaskTemplate, so Sync registers afresh the tasks an older one
    // made. 1.0.0 wrote none.
    internal const string TemplateRevision = "2";

    // Registered from XML for InteractiveToken: no password, no elevation. Each setting overrides a
    // default that would be wrong here: logon delay, no battery or idle stop, no 72 h limit, normal
    // priority. RunLevel restates its default as a security property, held by a check.
    private const string TaskTemplate =
@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Version>{3}</Version>
    <Description>Keeps the session non-idle so Teams presence stays Available.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger><UserId>{0}</UserId><Delay>PT10S</Delay></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{0}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
    </IdleSettings>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>{1}</Command><Arguments>{2}</Arguments></Exec>
  </Actions>
</Task>";

    // Schedule.Service, by CLSID: no ProgID lookup, and a type that is never null.
    private static readonly Type Scheduler =
      Type.GetTypeFromCLSID(new Guid("0F87369F-A4E5-4CFC-BD3E-73E6154572DD"));

    private readonly string sid, name, revision;

    // Task names are machine-wide: this one ends with the user's SID, like the per-user tasks of
    // Windows itself. The SID is the principal too, and resolves for any kind of account. The
    // revision is the template's, or an older one in the live checks.
    internal LogonTask(string prefix, string revision = TemplateRevision)
    {
      using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        sid = identity.User.Value;
      name = prefix + "-" + sid;
      this.revision = revision;
    }

    public TaskState Read(string exePath)
    {
      using (var com = new ComObjects())
      {
        object folder = RootFolder(com);
        object task = UnlessNotFound(() => com.Call(folder, "GetTask", name));
        if (task == null) return TaskState.None;
        object definition = com.Get(task, "Definition");
        if (!(bool)com.Get(task, "Enabled") || !AnyTriggerEnabled(com, definition))
          return TaskState.Off;
        string program = ProgramOf(com, definition);
        if (!File.Exists(program)) return TaskState.Stale;
        return IsSamePath(program, exePath) && RevisionOf(com, definition) != revision
               ? TaskState.Outdated : TaskState.On;
      }
    }

    // No account and no password: the task runs as the principal the document names.
    public void Register(string exePath)
    {
      using (var com = new ComObjects())
        com.Call(RootFolder(com), "RegisterTask", name, TaskXml(sid, exePath, revision),
                 TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
    }

    public void Delete()
    {
      using (var com = new ComObjects())
      {
        object folder = RootFolder(com);
        UnlessNotFound(() => com.Call(folder, "DeleteTask", name, 0));
      }
    }

    // Escaped: an ampersand in a folder name would break the document.
    internal static string TaskXml(string user, string exePath, string revision) =>
      string.Format(TaskTemplate, SecurityElement.Escape(user), SecurityElement.Escape(exePath),
                    Program.LogonSwitch, revision);

    private static object RootFolder(ComObjects com)
    {
      object service = com.Hold(Activator.CreateInstance(Scheduler));
      com.Call(service, "Connect");
      return com.Call(service, "GetFolder", @"\");
    }

    // A task switched off in the Task Scheduler UI, and one whose triggers all are, never runs.
    private static bool AnyTriggerEnabled(ComObjects com, object definition)
    {
      object triggers = com.Get(definition, "Triggers");
      int count = (int)com.Get(triggers, "Count");
      for (int i = 1; i <= count; i++)
        if ((bool)com.Get(com.Get(triggers, "Item", i), "Enabled")) return true;
      return false;
    }

    // The first action's program, expanded and unquoted as the scheduler reads it; null when the
    // action starts no program. The scheduler takes no task without an action.
    private static string ProgramOf(ComObjects com, object definition)
    {
      object action = com.Get(com.Get(definition, "Actions"), "Item", 1);
      if ((int)com.Get(action, "Type") != TASK_ACTION_EXEC) return null;
      return com.Get(action, "Path") is string path
             ? Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')) : null;
    }

    // Null from a task registered with none, as 1.0.0 did.
    private static string RevisionOf(ComObjects com, object definition) =>
      com.Get(com.Get(definition, "RegistrationInfo"), "Version") as string;

    // Normalised first, and blind to case as Windows paths are.
    private static bool IsSamePath(string program, string exePath) =>
      string.Equals(Path.GetFullPath(program), Path.GetFullPath(exePath),
                    StringComparison.OrdinalIgnoreCase);

    // A late-bound call's refusal arrives wrapped: null when it is about a missing task.
    private static object UnlessNotFound(Func<object> call)
    {
      try
      {
        return call();
      }
      catch (TargetInvocationException e) when (e.InnerException?.HResult == TaskNotFound)
      {
        return null;
      }
    }

    // Late bound: no interop assembly, and no Microsoft.CSharp for dynamic, which would stay loaded
    // in a daemon that reaches the scheduler once a session. Every object reached is released on
    // Dispose, not at a collection an idle daemon may never run: the service holds a connection.
    private sealed class ComObjects : IDisposable
    {
      private readonly List<object> held = new List<object>();

      internal object Hold(object value)
      {
        if (value != null && Marshal.IsComObject(value)) held.Add(value);
        return value;
      }

      internal object Get(object target, string property, params object[] args) =>
        Hold(target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, args));

      internal object Call(object target, string method, params object[] args) =>
        Hold(target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args));

      public void Dispose()
      {
        foreach (object value in held) Marshal.FinalReleaseComObject(value);
        held.Clear();
      }
    }
  }

  // Runs the logon task's calls off the UI thread and posts each answer back to it: whether a task
  // will start the tool at logon, or null when nothing was learned, so the tick stays; and whether
  // the user asked, as a Set does and the startup Sync does not. One call at a time, since two
  // would race over the task; a Set asked for meanwhile waits, because the startup Sync can still
  // be running when the user reaches the menu.
  internal sealed class AutoStartAgent
  {
    private readonly SynchronizationContext home;
    private readonly ILogonTask task;
    private readonly string exePath;
    private readonly Action<bool?, bool> answer;   // enabled, asked
    private readonly Func<Task> timeout;           // completes once a call has waited long enough
    private bool pending;
    private bool? wanted;                          // a Set waiting for the call in flight

    // The timeout is handed in, so Tests.cs decides when a wait runs out rather than racing it.
    internal AutoStartAgent(SynchronizationContext home, ILogonTask task, string exePath,
                            Action<bool?, bool> answer, Func<Task> timeout)
    {
      this.home = home;
      this.task = task;
      this.exePath = exePath;
      this.answer = answer;
      this.timeout = timeout;
    }

    // Once per session. The task stores an absolute path: when the copy it starts is gone, this one
    // takes its place; while that copy exists, the task is left to it. A task that starts this copy
    // from another template is registered afresh, dropping any hand edit.
    internal void Sync() => Run(asked: false, work: () =>
    {
      TaskState state = task.Read(exePath);
      if (state == TaskState.Stale || state == TaskState.Outdated) task.Register(exePath);
      return state == TaskState.On || state == TaskState.Stale || state == TaskState.Outdated;
    });

    // Only the last request counts.
    internal void Set(bool enabled)
    {
      wanted = enabled;
      Pump();
    }

    private void Pump()
    {
      if (pending || wanted == null) return;
      bool enabled = wanted.Value;
      wanted = null;
      Run(asked: true, work: () =>
      {
        if (enabled) task.Register(exePath); else task.Delete();
        return enabled;
      });
    }

    // Past the wait, the call is abandoned and the next request may run: should it still change the
    // task, the next launch's Sync shows how it ended up. A call that throws taught nothing. Either
    // leaves a line in the debugger output.
    private void Run(bool asked, Func<bool?> work)
    {
      pending = true;
      Task<bool?> call = Task.Run(work);
      Task.WhenAny(call, timeout()).ContinueWith(first =>
      {
        bool? outcome = null;
        if (first.Result != call) Native.Log("a Task Scheduler call went unanswered");
        else if (call.IsFaulted) Native.Log(call.Exception.InnerException);
        else outcome = call.Result;
        // Throws once the UI thread is gone, after a Quit while the call ran: unobserved, harmless.
        home.Post(delegate
        {
          pending = false;   // before the callback, so it may ask for the next thing
          answer(outcome, asked);
          Pump();
        }, null);
      }, TaskScheduler.Default);
    }
  }
}
