# perch

A notch for the Windows 11 taskbar that shows what your machine is doing: AI coding agents, builds, dev servers and git. It stays out of the way until something needs you.

![perch on the taskbar](docs/notch.png)

Click it, or press **Win+Alt+N**, and your agent sessions, builds, servers and repos float above the taskbar as cards:

![the cards](docs/cards.png)

## Install

```powershell
irm https://raw.githubusercontent.com/ashlrai/perch/main/get.ps1 | iex
```

This downloads the latest release into `%LOCALAPPDATA%\Programs\perch`, adds it to your PATH and starts the notch. No admin rights are needed. Right-click the notch to turn on **Start with Windows**.

Then connect Claude Code: run `perch hooks` and merge what it prints into `~/.claude/settings.json`.

**Requirements:** Windows 11 (Windows 10 works, but without the glass taskbar boxes) on x64. Optional: the [GitHub CLI](https://cli.github.com) for CI and pull request status.

The exes aren't code-signed yet, so Windows SmartScreen may ask you to confirm the first run.

## What it does

**Agents.** perch shows Claude Code, Codex, Gemini CLI, Aider and other agent CLIs running in your terminals and editors (a CLI started in the background by a browser or a script doesn't count). With the Claude Code hooks connected, each agent card shows:
- what the agent is working on
- what it's doing now, or what it said when it finished
- a tally of the turn ("6 reads · 3 edits · 4 commands")
- how full its context window is
- the files it changed, with **Review** and **Undo** (Undo stashes those files, so `git stash pop` brings them back)

You can also switch the card to a step-by-step timeline that flags loops, and type a quick reply.

![an agent card](docs/agent.png)

**Permission requests on the notch.** When Claude Code asks for permission, the notch glows amber and shows **Allow** / **Deny**. **Always** and **10 min** remember the answer for that command in that repository: a Bash rule matches the first two words, such as `npm test`, but never a chained command like `npm test && …`. If you don't answer within 45 s, Claude's own prompt takes over.

**Builds.** Put `perch` in front of a command and it shows up live in the notch, with a progress bar learned from earlier runs:

```powershell
perch run npm run build
perch watch npm test        # again on every file change; chimes only when the result flips
```

A failed run's card shows the end of its output with the error line picked out. **Fix with Claude** hands it to the Claude Code session in that repository. **Run again** reruns the command in a new terminal tab.

**Dev servers.** Local servers (node, bun, python, dotnet, docker, ollama…) appear with their folder and uptime. Stop them from the card, or Restart one you started with `perch run`.

**Git.** For repos you work in, the Git cards show the branch, changed files, and (with `gh`) the latest CI run and the pull request's review status. A CI run that starts failing chimes and flashes the notch red.

**Stays out of the way.** Hover the notch for a second line of detail. It tucks into the taskbar behind maximized windows, leaving a thin status line, and sinks out of sight in fullscreen. It follows the taskbar of the screen you're working on, and matches Windows' light/dark theme and accent colour. It chimes only when something finishes or needs you while you're in another window.

## Keys

With the cards open (click the notch or press Win+Alt+N):

| Key | |
|---|---|
| ← → / scroll | flip cards |
| ↑ ↓ / Tab | switch page (Agents, Builds, Ports, Git) |
| Enter | the card's main action: open the agent's window, run again, open the server |
| A / D | allow / deny a permission request |
| L / T | allow always / for 10 minutes |
| / | reply to the agent |
| V | summary ↔ timeline; PgUp/PgDn scroll it |
| R | review an agent's changes; run a build again |
| U U | undo (stash) an agent's changes |
| S S / R | stop / restart a dev server |
| F | fix a failed build with Claude |
| Esc | close |

## Commands

| | |
|---|---|
| `perch run [-n label] <cmd>` | Run a command in your terminal as usual and show it in the notch. Ctrl+C still works. |
| `perch watch [-n label] <cmd>` | Run it again whenever a file in the folder changes. |
| `perch note "deployed 🚀"` | Flash a message in the notch. |
| `perch hooks` | Print the Claude Code hooks config. |

## Settings

Right-click the notch → **Edit config** (`%APPDATA%\perch\config.ini`), then **Reload config**:

```ini
hotkey = win+alt+n         # or ctrl+alt+space, ctrl+shift+f9, off
sound = on                 # chime when something finishes or needs you while you're elsewhere
theme = auto               # follows the taskbar | dark | light
accent = auto              # Windows accent colour | none | #RRGGBB
maximized = tuck           # tuck into the taskbar behind maximized windows | stay
hide_fullscreen = true     # sink out of sight in fullscreen
monitor = follow           # follow the screen you're working on | primary
backdrop = plain           # dim the desktop behind the cards | glass (dim and blur)
taskbar = system           # system | clear (see below)
motion = low               # no spinners, 1 refresh a second
repo = C:\code\my-app      # always list this repo
port = 4040                # treat this port as a dev server
context_window = 200000    # override the context meter's window size
```

### The see-through taskbar (optional)

perch draws glass boxes behind the taskbar's three groups. To see them, the taskbar's own background has to be transparent. Turn this on in the right-click menu (**Clear the taskbar background**). perch then loads a small helper, `perch-tap.dll`, into Explorer through the XAML diagnostics API, the same mechanism TranslucentTB uses. The helper hides only the taskbar's two background rectangles, and puts them back when perch quits. Don't run TranslucentTB or another taskbar styler at the same time. Windows 11 only.

## Privacy

perch runs entirely on your PC and sends nothing anywhere: no telemetry, no accounts.

- **What it reads:** your process list and listening ports (to find agents and dev servers); `git status` and `git diff` for repos you work in; and Claude Code's local transcripts in `~/.claude/projects` (for the summaries, context meter and token totals).
- **What talks to the network:** only `gh`, and only if you have it installed and signed in. It's used to fetch CI and pull request status from GitHub.
- **How the parts talk:** `perch` and the notch exchange messages over UDP on `127.0.0.1`. Answers to permission requests carry a one-time token, so other programs can't approve them.

It never takes screenshots of your windows. Its own files are `%APPDATA%\perch` (settings, run history, approval rules) and `%TEMP%\perch.log` (errors).

## Uninstall

Right-click the notch → **Quit perch**. Then delete `%LOCALAPPDATA%\Programs\perch` and `%APPDATA%\perch`, and remove the first folder from your user PATH.

## Build from source

You need the .NET 9 SDK and Visual Studio's **Desktop development with C++** workload (the Native AOT linker and the taskbar helper need it).

```powershell
./install.ps1                       # build, install to %LOCALAPPDATA%\Programs\perch, start
dotnet test tests/Perch.Tests       # tests
```

`src/Perchd` is the notch (`perchd.exe`), `src/Perch` the command line (`perch.exe`), `src/Shared` what both use, and `src/TaskbarTap` the optional Explorer helper. See [CONTRIBUTING.md](CONTRIBUTING.md).

### Why it's light

- **Native AOT C#:** a ~2 MB exe with no runtime, Electron or WebView. About 10 MB of memory while collapsed, and well under 1% of a core at idle.
- **Raw Win32:** GDI+ shapes and DirectWrite text, drawn into per-pixel-alpha layered windows.
- **Event-driven:** nothing redraws while idle. The taskbar layout is re-read only when windows come and go, and transcripts are read incrementally, only the lines added since the last read.
- **Cheap polling:** messages from `perch` arrive as window messages, with no listener thread. While collapsed, perch checks listening ports every 4 s and takes a process snapshot every 12 s.

## License

MIT. See [LICENSE](LICENSE).
