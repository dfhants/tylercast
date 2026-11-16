# TylerCast for Windows: building and testing

A .NET 10 port of the Mac app (`../Sources`), with the same design: SSDP discovery, its own
MPEG-TS muxer and pacer, a small HTTP server only the chosen TV may use, and DLNA
AVTransport control. For using the app, see the [README](../README.md).

All of it builds and its core tests run on a Mac (`EnableWindowsTargeting`); the Windows
parts run on Windows, in CI or on a PC.

## Layout

| Project | What |
|---|---|
| `src/TylerCast.Core` | Portable engine: discovery, SOAP, TS muxer, pacer, stream server, session. Runs on macOS too |
| `src/TylerCast.Windows` | Screen capture (Desktop Duplication, GDI fallback), Media Foundation H.264 and AAC encoders (hardware encoders first, such as Intel Quick Sync; Microsoft's software encoder as the fallback), WASAPI loopback sound, self-test |
| `src/TylerCast.App` | The tray app (WPF, Windows 11 Fluent theme): `TylerCast.exe` |
| `src/TylerCast.Cli` | `tylercast.exe`: `discover`, `play`, `serve`, `record`, `diag`, `selftest` |
| `tests/TylerCast.Core.Tests` | xUnit tests of the engine, including a fake TV and security tests |
| `installer/TylerCast.iss` | Inno Setup per-user installer |
| `tools/` | Icon generation, installer test, test-round tooling |

## Build and test

```bash
# .NET 10 SDK
dotnet build TylerCast.slnx
dotnet test TylerCast.slnx
# the tray app as one self-contained exe:
dotnet publish src/TylerCast.App -r win-x64 -c Release --self-contained -p:PublishSingleFile=true -o out/app
```

`TylerCast.exe --screenshot DIR` renders the panel in every state, light and dark, with
made-up TVs: CI does this on every push, so the layout can be checked without a PC.

On Windows, `tylercast.exe selftest DIR` checks what it can without a TV (encoders,
capture, sound) and `tylercast.exe diag` describes the PC; both are useful in bug reports.

## CI and releases

- `.github/workflows/windows.yml` (every push touching `windows/`): build, tests,
  installer built and then installed, run and uninstalled on a Windows runner, panel
  screenshots, self-test.
- `.github/workflows/release.yml` (version tags): the Windows installer and the Mac app,
  published to GitHub Releases with SHA-256 hashes. `v0.3.0-beta1`-style tags make
  pre-releases.

## Notes

- Windows' own Dolby Digital (AC-3) encoder can't be used by other apps (it ends the
  process), so sound is AAC.
- Windows asks about the firewall the first time a new `TylerCast.exe` listens; until it's
  answered the TV can't connect, so the session keeps asking the TV for up to 45 s.
- [PORTING.md](PORTING.md) records which Swift commit the port is up to date with.
