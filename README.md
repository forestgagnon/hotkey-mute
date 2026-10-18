# Windows Hotkey Mute

A small Windows tray app that lets you register global hotkeys to mute/unmute individual programs, independently of each other.

## Usage

1. Run `HotkeyMute.exe`.
2. Click **Add...**, pick a program that is playing audio (or type a name such as `game.exe`), click the hotkey box and press the key combination.
3. Press the hotkey anywhere to toggle that program's mute. Double-click a row (or **Toggle now**) to toggle from the window.

Several programs can share one hotkey (they are marked with a warning sign in the list). Pressing it toggles all of them, and each flips from its own current state. Combined with **Toggle now** this lets you set up programs that swap back and forth.

Double-click a row to edit its program and hotkey.

Closing the window keeps the app running in the system tray (right-click the icon for Open / Exit). Optional: show notifications, start with Windows.
Bindings are stored in `%APPDATA%\HotkeyMute\settings.txt`.

All audio sessions belonging to a program (e.g. every Firefox or Chrome process) are muted together.

## How it works / anticheat note

- Muting uses the Windows Core Audio session API (`ISimpleAudioVolume`), the same API the Volume Mixer uses.
- Hotkeys are detected with a passive `WH_KEYBOARD_LL` hook installed in this app's own process.
- **No DLL injection**, no reading or writing of other processes' memory, no handles opened to other processes. Other programs are identified by process name only.
- Some strict anticheat systems may still dislike any global keyboard hook. If in doubt, exit the app while playing.

## Build

Requirements: Windows with PowerShell. No SDK is needed; the build uses the C# compiler (`csc.exe`, .NET Framework 4.x) that ships with Windows.

```powershell
git clone https://github.com/forestgagnon/windows-hotkey-mute
cd windows-hotkey-mute
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

This generates `HotkeyMute.ico` and builds `HotkeyMute.exe` in the same folder.

Equivalent manual compile (needs an existing `HotkeyMute.ico`, or drop `/win32icon`):

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ `
    /out:HotkeyMute.exe /win32icon:HotkeyMute.ico `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll HotkeyMute.cs
```

The source is plain C# 5, so it also builds with newer compilers.
