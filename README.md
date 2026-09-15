# Teams Keep-Alive

A tiny Windows 11 tray utility that keeps your session from going idle, so Microsoft Teams
presence stays **Available** instead of drifting to **Away** while you read, think, or watch
something.

- **One ~60 KB executable.** No installer, no runtime to download, no administrator rights.
- **Idle-aware.** Nothing is sent while you use the machine, and it sleeps while paused, locked, or
  while a remote session is disconnected.
- **Checks its own work.** If the keystroke stops reaching Windows, or the tool itself fails, the
  tray icon turns red.
- **Auto-stop** after a few hours or at a set time.
- **English and French**, picked from the Windows display language.

![The tray menu in its three states — running, paused and blocked — in both
Windows themes](screenshot.png)

## How it works

Teams decides you have stepped away by reading the Windows idle timer. After a minute without real
input, Teams Keep-Alive sends a single **F15** keystroke: the key exists in Windows but on almost
no keyboard, so applications ignore it, and the idle timer resets all the same.

Nothing in it is specific to Teams: Slack, Zoom and any other app that reads the idle timer see
you as present too. PowerToys Awake is a different tool: it keeps the machine from sleeping, but
Teams still turns Away.

## Install

Download `TeamsKeepAlive.exe` from the
[latest release](https://github.com/NSO73/teams-keep-alive/releases/latest), put it anywhere in
your profile, and double-click it. To start it at logon, tick **Start with Windows** in its menu.

The binary is not code-signed, so SmartScreen asks for confirmation the first time (*More info* →
*Run anyway*). Every release ships a SHA-256 checksum, which the first command below compares with
the download (it prints `True`), and releases published while this repository is public are also
attested by the workflow that built them:

```powershell
(Get-FileHash .\TeamsKeepAlive.exe).Hash -eq (Get-Content .\TeamsKeepAlive.exe.sha256).Split(' ')[0]
gh attestation verify .\TeamsKeepAlive.exe --repo NSO73/teams-keep-alive
```

## Using it

Right-click the tray icon:

| Entry | What it does |
| --- | --- |
| **Active** | Pause or resume; double-clicking the icon does the same. |
| **Auto-stop** | Pause after 1, 4 or 8 hours, at a time typed in such as `18:30`, or **Never**. |
| **Start with Windows** | Start at logon, through a per-user scheduled task. |
| **Quit** | Stop and remove the icon. |

The icon is green when running, grey when paused, and red when the keystroke is blocked or the
tool hit an internal error; hovering it says which, and until when it runs. Launching the
executable again opens the menu of the copy already running.

## Good to know

- **No administrator rights, ever.** Settings live in `HKCU\Software\TeamsKeepAlive`. To remove
  every trace, untick **Start with Windows**, quit, and delete the `.exe` and that key.
- **A status set by hand in Teams**, such as Do Not Disturb, is honoured.
- **An elevated window in the foreground swallows the keystroke** (UIPI). If that lasts, the icon
  turns red.
- **Start with Windows goes through the Task Scheduler.** Where a policy forbids scheduled tasks,
  ticking it raises a notification instead, and the menu marks the entry as failed.
- **The whole session stays non-idle**, so the screen saver, the automatic lock, the display
  turning off and sleep all wait too. Check your organisation's policy, and lock your workstation
  when you walk away.

## Build it yourself

Needs only the C# compiler of Visual Studio or its free Build Tools: no SDK, no NuGet, no project
file.

```text
build.cmd             builds bin\TeamsKeepAlive.exe
build.cmd test        runs the checks in tests\
build.cmd test live   adds checks against the Task Scheduler, the registry and this session
```

CI runs the checks, not the live ones, before every build.

## Licence

[WTFPL v2](LICENSE) — do what the fuck you want to.

Microsoft Teams is a trademark of Microsoft Corporation. This project is not affiliated with,
endorsed by, or sponsored by Microsoft.
