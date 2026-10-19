# claude-in-soundmixer-patch

**This patch is for Windows machines.** (Windows 10 and 11.) It does not work on
macOS or Linux; see [macOS and Linux](#macos-and-linux) for why.

A patch for the Claude desktop app on Windows. It does three things:

1. It gives Claude **one permanent "Claude" slider in the Windows Volume Mixer**, which
   stays there even while Claude is silent.
2. It puts the audio of Claude's **Terminal panel** (and everything you run in it)
   under that same slider.
3. It plays a **chime when Claude finishes a prompt** (and, if you want, when Claude
   needs your permission). The chime follows the slider: it gets quieter when you turn
   Claude down and stays silent when you mute Claude.

This is an unofficial community patch. It is not made by or affiliated with
Anthropic. It changes no Claude files: it only adds hooks to your Claude Code
settings and runs a small helper program next to them.

## Quick start

You need [Git for Windows](https://git-scm.com/download/win) for `git clone` (or
download the ZIP instead, see below). In PowerShell:

```powershell
git clone https://github.com/Thmyris/claude-in-soundmixer-patch.git; if ($?) { cd claude-in-soundmixer-patch; .\install.cmd }
```

Answer its one question, then restart any open Claude Code sessions. That's it.

**Without Git:** on the GitHub page click **Code > Download ZIP**, unzip it, and
double-click `install.cmd` in the unzipped folder.

**Update:** run `git pull` in the folder, then `.\install.cmd` again.
**Remove:** run `.\uninstall.cmd`.

## What the patch does

Without the patch:

- Claude only shows up in the Volume Mixer for a few minutes after it has played a
  sound. After that, Chromium (which Claude is built on) drops its audio session and
  the slider disappears, so there is nothing to turn down until Claude is noisy again.
- Anything you run in Claude's **Terminal panel** (the shell on the side) gets its own
  "Windows PowerShell" / "cmd" slider. The Claude slider has no effect on it.
- Claude Code has no sound when it finishes a prompt or needs you.

With the patch:

- **A permanent "Claude" slider.** It has Claude's name and icon and stays in the mixer
  for as long as the Claude app is running, even while Claude is silent.
- **One slider for everything Claude does.** Claude's own audio, the Terminal panel
  and everything started from it, and the chime all share that one slider. Moving it
  or muting it applies to all of them. Changing any of them in
  *Settings > Sound > App volume* updates the rest.
- **A chime.** A chime plays when Claude finishes a prompt. If you want (you are asked
  during install), it also plays when Claude needs your permission or is waiting for
  you, like Cursor does. It plays at the Claude slider's level and stays silent while
  Claude is muted.
  - The sound is `sounds\chime.mp3` (or `sounds\chime.wav`) in this folder. To use a
    different sound, replace that file and run `install.cmd` again. If the `sounds`
    folder has neither file, the patch installs only the slider.

## Install in detail

1. Get the files: `git clone https://github.com/Thmyris/claude-in-soundmixer-patch.git`,
   or **Code > Download ZIP** on GitHub and unzip it.
2. Run **`install.cmd`** (double-click it, or `.\install.cmd` in PowerShell). It asks
   one question (skipped if there is no chime sound):
   - *Also play the chime when Claude needs your permission or is waiting for you?*
3. Restart any open Claude Code sessions so they pick up the new hooks.

The slider appears when your next Claude Code session starts. If the Claude app was
already open, it appears right away.

The same thing without prompts, from PowerShell in the repository folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\src\patch.ps1 -WaitingChime yes
```

| Parameter | Values | Meaning |
|---|---|---|
| `-Chime` | `yes` (default), `no` | chime when a prompt finishes; `no` installs only the slider |
| `-WaitingChime` | `ask` (default), `yes`, `no` | also chime on permission prompts and when Claude is waiting |
| `-ClaudeDir` | a folder | Claude Code's config folder; default `$env:CLAUDE_CONFIG_DIR`, else `%USERPROFILE%\.claude` |
| `-Uninstall` | | remove everything |

Running the patch again updates the install and lets you change your answers. It
needs no admin rights.

## Uninstall

Double-click **`uninstall.cmd`**, or:

```powershell
powershell -ExecutionPolicy Bypass -File .\src\patch.ps1 -Uninstall
```

This removes the hooks from `settings.json` (it keeps everything else in that file),
stops the watcher and deletes the installed files.

## How it works

The patch compiles `src/claude-chime.cs` with the .NET Framework compiler that comes
with Windows, which gives `%USERPROFILE%\.claude\sounds\claude-chime.exe`. It then
adds Claude Code hooks to
`%USERPROFILE%\.claude\settings.json`, after backing that file up to
`settings.json.bak-<date>`:

| Hook | Command | Why |
|---|---|---|
| `SessionStart` | `claude-chime.exe --start` | starts the mixer watcher (always installed) |
| `Stop` | `claude-chime.exe` | chime when a prompt finishes (if there is a chime sound) |
| `Notification` | `claude-chime.exe` | chime when Claude needs you (if you chose it) |

**The watcher** is a small background process that runs only while the Claude desktop
app is open. It exits when the app closes, and only one copy runs at a time. It uses
about 0.2% of one CPU core and about 25 MB of RAM. It does the following once a
second, and also immediately whenever Windows reports that any app has opened an
audio session:

1. It holds a silent audio stream of its own on the default output device. The stream
   is opened but never started, so it plays nothing. It is labelled "Claude" with
   Claude's icon, and this is the slider that stays in the mixer.
2. It finds the audio sessions of the Claude app and of every process the app started,
   such as Terminal panel shells, what you run in them, and the chime. It gives them
   all the same grouping ID as its own stream, and the Volume Mixer draws sessions
   that share a grouping ID as one slider.
3. It keeps all of them at the same level and mute. If one of them changes, the rest
   follow. A new session is set to the Claude level within milliseconds, before it
   starts playing. This matters after a Claude update: Windows saves per-app volume
   by exe path, so it opens the new build's audio at 100%. Without the immediate
   check, Claude's next sound would start at full volume and only drop when the next
   once-a-second check ran.
4. Windows remembers volume per program. So if an unrelated PowerShell (for example in
   Windows Terminal) inherits the Claude level that way, the watcher puts that
   PowerShell back to 100%.

**The chime** is `sounds\chime.mp3` (or `sounds\chime.wav`) from this folder, copied to
`%USERPROFILE%\.claude\sounds\`. When a hook runs, it reads the
current Claude level, plays the sound at that level, and does nothing while Claude is
muted.

## Requirements

- Windows 10 or 11 (this patch is for Windows machines).
- The Claude desktop app, using Claude Code (the Code tab). The hooks run in
  Claude Code sessions.
- .NET Framework 4.x, which is built into Windows 10 and 11.

## macOS and Linux

This patch only works on Windows. Nothing in it runs on macOS or Linux:

- The watcher is built on Windows Core Audio (the per-app audio sessions behind the
  Volume Mixer). It is compiled with the .NET Framework compiler that comes with
  Windows. The installer is a Windows PowerShell script started from `.cmd` files.
- **macOS** has no per-app volume mixer, so there is no Claude slider to create.
  You would need a third-party audio tool for per-app volume.
- **Linux** has per-app volume through PulseAudio or PipeWire (for example
  `pavucontrol`), so a port could be written, but it would be a separate program.
  Also, Anthropic does not make an official Claude desktop app for Linux.

You can make the chime part on its own on macOS or Linux by hand. Add a Claude Code
`Stop` hook that plays a sound, for example `afplay /path/to/chime.wav` on macOS or
`paplay /path/to/chime.wav` on Linux. It will not follow a Claude volume slider.

## Troubleshooting

- Run `%USERPROFILE%\.claude\sounds\claude-chime.exe --why`. It shows which level the
  chime would use, where that level came from, and whether the watcher is running.
- The watcher logs to `%USERPROFILE%\.claude\sounds\watcher.log`.
- The Claude slider only exists while the watcher runs. The watcher starts with your
  first Claude Code session after the Claude app opens. If you only use the chat part
  of the app, it never starts.
- If you downloaded the ZIP and Windows blocks `install.cmd`, right-click the ZIP >
  *Properties* > *Unblock* before unzipping.

## Files

| Path | What it is |
|---|---|
| `install.cmd`, `uninstall.cmd` | double-click wrappers around `src/patch.ps1` |
| `src/patch.ps1` | the patch: builds, installs, edits `settings.json`, uninstalls |
| `src/claude-chime.cs` | the chime and the mixer watcher (one program) |
| `sounds/chime.mp3` or `sounds/chime.wav` | the chime sound |
