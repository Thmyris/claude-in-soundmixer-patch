# claude-in-soundmixer-patch

**This patch is for Windows machines.**

A soundmixer patch for the Claude desktop app. It does two things:

1. **Permanent "Claude" slider in the Windows Volume Mixer**.
   So if Claude starts playing music from youtube while testing your browser extension app, or the terminal, you can mute it. It also auto updates the mixer value when claude app auto updates itself, so you'll never get blasted with high volume. [looking at you discord]
2. Optional: **Cursor finish chime when Claude finishes a prompt**.
   The chime follows the slider.
   
## Quick start

**Install:**
```powershell
git clone https://github.com/Thmyris/claude-in-soundmixer-patch.git; if ($?) { cd claude-in-soundmixer-patch; .\install.cmd }
```

**Remove:** 
```powershell
.\uninstall.cmd
```

## Troubleshooting

- The Claude slider only exists while the watcher runs. The watcher starts with your
  first Claude Code session after the Claude app opens. If you only use the chat part
  of the app, it never starts.

## Files

| Path | What it is |
|---|---|
| `install.cmd`, `uninstall.cmd` | double-click wrappers around `patch.ps1` |
| `src/patch.ps1` | the patch: builds, installs, edits `settings.json`, uninstalls |
| `src/claude-chime.cs` | the chime and the mixer watcher (one program) |
| `sounds/chime.mp3` or `sounds/chime.wav` | the chime sound |