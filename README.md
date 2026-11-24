# notchbar

A dev notch for the Windows taskbar. The taskbar's three groups (widgets, Start and apps, tray) sit in floating glass boxes, and a small notch rises out of the top of the center box to show what your machine is doing: AI agents, builds, dev servers and git. Click it and your agent sessions, builds, servers and repos float above the taskbar as cards you can flip through.

```
                       ╭─────────────────────────────╮
                       │ ✳ Bash  npm run deploy -- --prod  ⬡ │   ← the notch: one line of status
  ╭──────────╮ ╭───────╯                             ╰──────────╮ ╭──────────────╮
  │ 11°C     │ │  ⊞  Search   apps apps apps apps apps          │ │ ⌃ ᯤ 🔊 12:04 │   ← real taskbar icons on glass boxes
  ╰──────────╯ ╰────────────────────────────────────────────────╯ ╰──────────────╯

  click the notch → it becomes a page switcher (Agents · Builds · Ports · Git) and the
  page's items float above it as cards: one per agent window, build, server or repo
```

**The cards.** Each agent session gets a card showing a live picture of the editor or terminal it runs in, next to its last prompt, the command it's running and its state. The focused card sits in front and the rest lean back on either side. Flip with ← → or the scroll wheel, switch pages with ↑ ↓ or the tabs, press Enter or click "Open in VS Code ↗" to jump to that window, and Esc or click outside to close. Builds, ports and repos get cards too (progress and exit code; Open ↗; branch and changed files).

**Keyboard first.** Press **Win+Alt+N** anywhere to open the cards (again to close). If an agent is waiting for approval, it opens straight onto that agent's card: press **A** to allow or **D** to deny, and the cards close again. On the Builds page, **R** or Enter runs the focused command again.

**Hover to peek.** Hovering the notch lifts it slightly and shows a second line: the prompt an agent is working on, a build's folder and how far along it is, or which ports are open.

**Out of the way.** When a maximized window is in front, the notch tucks into the taskbar so it doesn't cover the bottom of the window. Only a 6 px lip stays visible, with a status line: amber pulse when an agent needs you, progress or a moving scanner while something runs, green or red when it finishes. Hover the lip and the notch pops back up. In fullscreen (F11, videos, games) the whole island sinks below the screen edge, and it rises again when you leave fullscreen.

**It tells you when to look.** While an agent needs you, the notch glows amber; when something finishes, a ring ripples out from it (green, or red for a failure). If you're in another window, you also hear a chime (`sound = off` turns it off).

**Allow / Deny from the notch.** With the hooks below, when Claude Code asks for permission the notch shows Allow and Deny right in the collapsed pill and on the agent's card. If you don't answer there within 45 s, Claude's own prompt takes over.

`nb run` remembers how long each command took last time, so the next run of the same command shows a progress rail and `0:08 / ~0:31`. The Builds page keeps your last 60 runs: one card per command shows its recent history as bars (height = duration, green or red = result) and a **Run again** button that reruns it in a new Windows Terminal tab in the same folder.

**Context meter.** With the hooks connected, each Claude Code card shows how full the session's context window is and how many tokens the session has used so far. It reads them from the session's transcript, parsing only the lines added since the last read.

## Install

Requires the .NET 9 SDK and Visual Studio's **Desktop development with C++** workload (the Native AOT linker and the taskbar helper DLL need it).

```powershell
./install.ps1
```

This builds both exes, puts them in `%LOCALAPPDATA%\Programs\notchbar`, adds that folder to your PATH and starts the notch. Right-click the notch to turn on **Start with Windows**.

### The see-through taskbar

notchbar draws its glass boxes *behind* the real taskbar, so it makes the taskbar's own background invisible by itself; no TranslucentTB or Windhawk needed. The Windows 11 taskbar is XAML inside Explorer, and its background is two rectangles (`BackgroundFill` and `BackgroundStroke`). At startup notchbar loads a small helper, `notchbar-tap.dll`, into Explorer through the XAML diagnostics API (the one Visual Studio's Live Visual Tree uses). The helper sets just those two rectangles to invisible and touches nothing else. When notchbar quits, the helper puts the background back; when Explorer restarts, notchbar clears the new taskbar again. To keep the normal taskbar, set `taskbar = system` in the config and restart notchbar.

Don't run TranslucentTB (or another taskbar styler) at the same time: they use the same mechanism and get in each other's way.

The box positions come from the taskbar itself (UI Automation), so they follow pinned apps, opening windows and display scaling.

## Use

| | |
|---|---|
| `nb run npm run build` | Runs the command in your terminal as usual and shows it live in the notch. Shows ✓ or ✕ with the exit code when it ends. Ctrl+C still works. |
| `nb run -n tests cargo test` | The same, with a custom label. |
| `nb note "deployed 🚀"` | Shows a message in the notch for a few seconds. |
| `nb hooks` | Prints the Claude Code hooks config. Paste it into `~/.claude/settings.json` and the notch shows when Claude is working, when it **needs you** (amber pulse, with Allow / Deny), and when it's done. If you added the hooks before, add the new `PermissionRequest` entry. |

Detected automatically, with no setup:
- **Dev servers**: local listeners from node, bun, deno, python, dotnet, java, docker, wsl, ollama and others, plus common dev ports. Click one to open `localhost:<port>`.
- **Shells**: interactive shells open in Windows Terminal, VS Code, Cursor, JetBrains IDEs and similar.
- **Agents**: running `claude`, `codex`, `aider`, `gemini`, `opencode` and similar processes, each with its own line-art glyph, and the editor or terminal it runs in (VS Code, Cursor, Windows Terminal, JetBrains, ...).
- **Compilers**: shows "cargo busy" (and the same for msbuild, ninja, go and others) while one is running.
- **Git**: branch, changed and untracked files, and ahead/behind counts for repos you've used `nb run` in or an agent has worked in. Click a repo to open a terminal there.

Click a finished task or agent to dismiss it.

## Config

Right-click → **Edit config** (`%APPDATA%\notchbar\config.ini`):

```ini
repo = C:\code\my-app      # always show this repo
port = 4040                # treat this port as a dev server
hide_fullscreen = true     # sink away during games, fullscreen video, F11 and presentations
maximized = tuck           # tuck into the taskbar while a maximized window is in front | stay
motion = low               # no spinners; refresh 1×/s while busy
taskbar = clear            # hide the Windows taskbar's background (default) | system
backdrop = plain           # dimmed desktop behind the cards (default) | glass (dimmed and blurred)
hotkey = win+alt+n         # e.g. ctrl+alt+space, ctrl+shift+f9, or off
sound = on                 # chime when an agent finishes or needs you while you're elsewhere
theme = auto               # follows the Windows taskbar theme | dark | light
accent = auto              # Windows accent colour | none | #RRGGBB
monitor = follow           # move to the taskbar of the screen you're working on | primary
context_window = 200000    # override the context meter's window size (default: from the model)
```

Light/dark theme and accent colour changes in Windows settings apply right away.

## Why it's light

- **Native AOT C#**: one ~1.8 MB exe with no runtime install, JIT, Electron or WebView. About 7 MB private memory while collapsed, and well under 1% of one core at idle (a spinning agent icon costs about 1-2%). Opening the cards temporarily adds the card images, window pictures and blurred backdrop; all of it is freed when they close.
- **Event-driven taskbar tracking**: the taskbar is re-read through UI Automation only when an app window appears, closes or comes to the front (plus a 15 s backstop for pin changes), so Explorer does no extra work at idle.
- **DirectWrite text** (Segoe UI Variable, Cascadia Mono, colour emoji) drawn through a Direct2D DC render target bound to just each string's pixels, on top of GDI+ shapes. Set `NOTCHBAR_GDIPLUS_TEXT=1` to fall back to GDI+ text.
- **Raw Win32 + GDI+** drawing into a per-pixel-alpha layered window. The window is resized to the pill every frame, so the rest of the screen still gets your clicks.
- **Event-driven**: `nb` sends one UDP datagram to `127.0.0.1:47811`. The daemon receives it with `WSAAsyncSelect`, so it arrives as a window message and needs no listener thread.
- **Draws only on change**: nothing renders while idle. Spinners redraw at 20 fps for about 0.3 ms per frame.
- **Cards are pictures**: each card is rendered once into an image and only redrawn when its contents change; animation frames just place the images (the side cards' tilt is drawn as thin strips, since GDI+ has no perspective transform). Frames are paced to the display with `DwmFlush`, and nothing redraws while the cards sit still. Set `NOTCHBAR_PROF=1` to log frame times to `%TEMP%\notchbar-prof.txt`.
- **Work done while you hover**: the blurred backdrop and the agent window pictures (`PrintWindow`) are captured on worker threads while the cursor is on the notch, so the click doesn't wait for them; they're dropped if you don't open.
- **Free fades**: the dimmed backdrop is a plain layered window painted once and faded with its window alpha, so it costs nothing per frame. The side boxes are click-through windows redrawn only when the taskbar layout changes.
- **Cheap polling**: while collapsed it reads the TCP listener table every 4 s (about 1 ms) and takes a full process snapshot every 12 s to spot agent CLIs. `git status` runs only while the panel is open.
- **No blur by default**: the desktop behind the cards is only dimmed. With `backdrop = glass`, the screen is captured once, shrunk 12x, box-blurred, darkened and scaled back up.
- Hides itself when a fullscreen app is in front.
