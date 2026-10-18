# Hotkey Mute

> # ⚠️ WINDOWS ONLY
> **This app only runs on Windows (10/11).** It uses Windows-specific audio and keyboard APIs. There is no macOS or Linux version.

A small tray app that lets you register global hotkeys to mute/unmute individual programs, independently of each other. Supports saved configs and built-in mute-all actions.

## Install

1. Go to the [Releases page](https://github.com/forestgagnon/hotkey-mute/releases) and download `HotkeyMute.exe` from the latest release.
2. Put it anywhere you like and run it. There is no installer, and no admin rights are needed.
3. Windows SmartScreen may warn about an unrecognized app because the exe is not code-signed. Choose **More info** > **Run anyway**.

To remove it, exit from the tray menu, untick **Start with Windows**, and delete the exe. Settings live in `%APPDATA%\HotkeyMute\settings.txt`.

## Limitations

- Hotkeys don't fire while an administrator-elevated window has focus (a Windows restriction on keyboard hooks). Run Hotkey Mute as administrator if you need that.
- Muting applies to the audio streams a program has when you press the key. A stream it opens later is not muted until you toggle again.
- Programs are matched by process name (e.g. `firefox.exe`), so a launcher that hands off to a differently named process won't match.

## How it works / anticheat note

- Muting uses the Windows Core Audio session API (`ISimpleAudioVolume`), the same API the Volume Mixer uses.
- Hotkeys are detected with a passive `WH_KEYBOARD_LL` hook installed in this app's own process, on its own thread.
- **No DLL injection** and no reading or writing of other processes' memory. Other programs are identified by process name only.
- Some strict anticheat systems may still dislike any global keyboard hook. If in doubt, exit the app while playing.

## Build

Requirements: Windows with PowerShell. No SDK is needed; the build uses the C# compiler (`csc.exe`, .NET Framework 4.x) that ships with Windows.

```powershell
git clone https://github.com/forestgagnon/hotkey-mute
cd hotkey-mute
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

This generates `HotkeyMute.ico` and builds `HotkeyMute.exe` in the same folder.

Equivalent manual compile (needs an existing `HotkeyMute.ico`, or drop `/win32icon`):

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ `
    /out:HotkeyMute.exe /win32icon:HotkeyMute.ico `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll HotkeyMute.cs
```

The source is plain C# 5, so it also builds with newer compilers. The version shown in the window title comes from the `AssemblyVersion` line near the top of `HotkeyMute.cs`.

## License

[MIT](LICENSE)
