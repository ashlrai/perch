# Contributing to perch

Thanks for helping. perch is small on purpose: one native exe for the notch, one for the command line, no runtime, nothing running while idle. Changes should keep it that way.

## Setup

- Windows 11 x64
- .NET 9 SDK
- Visual Studio 2022 (or Build Tools) with **Desktop development with C++**, for the Native AOT linker and the taskbar helper

```powershell
./install.ps1                    # build both exes + the helper, install, restart the notch
dotnet test tests/Perch.Tests    # run the tests
```

`install.ps1` replaces the installed perch with your build, so you can try a change right away. Set `PERCH_PROF=1` to log frame times to `%TEMP%\perch-prof.txt`. Errors go to `%TEMP%\perch.log`.

## Layout

| | |
|---|---|
| `src/Perchd` | the notch (`perchd.exe`): windows, drawing, cards, probes |
| `src/Perch` | the command line (`perch.exe`): `run`, `watch`, `note`, `hooks`, and the Claude Code hook (`agent`) |
| `src/Shared` | the message format and the approval rules, used by both |
| `src/TaskbarTap` | the optional C++ helper that clears the taskbar background |
| `tests/Perch.Tests` | xUnit tests |

## Guidelines

- **Idle means idle.** Don't add timers or polling that run while nothing is happening. Do slow work (processes, git, gh, files) on a worker thread, then post a message back to the UI thread.
- **No new runtime dependencies.** Win32, GDI+, DirectWrite and Direct2D are called directly (see `Native.cs`, `Dw.cs`). Everything must stay Native AOT compatible: no reflection-based JSON serialization, no WinForms or WPF.
- **Be careful with anything that acts for the user.** perch can answer permission prompts, type into windows, stash files and stop processes. Such features need a guard: a one-time token, a check that the target is really what we think, or a second press to confirm. Explain the guard in a comment.
- **Tests** for logic that can be tested without a screen: parsing, matching, formatting.
- **Comments** explain why, not what.

## Pull requests

Keep them focused. Say what changed and how you tried it, and include a screenshot or GIF for anything visible. CI builds and tests every PR.

## Reporting bugs

Use the issue templates. Include your Windows build (`winver`), your display scaling, and anything relevant from `%TEMP%\perch.log`.
