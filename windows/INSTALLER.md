# QuackSnap Windows installer

The installer is built with [Velopack](https://velopack.io) — it produces a
`Setup.exe`, a portable zip, and the update packages needed for auto-updates.
Velopack builds Windows releases from **any OS** (macOS/Linux included), so the
whole thing is reproducible in CI.

## Build it

From the repo root:

```sh
./windows/build-installer.sh [version]        # macOS / Linux (cross-compiles)
```

or on Windows:

```powershell
powershell -ExecutionPolicy Bypass -File windows\build-installer.ps1 -Version 1.0.0
```

Both scripts:

1. `dotnet tool restore` — installs the pinned `vpk` tool from `.config/dotnet-tools.json`.
2. `dotnet publish -c Release -r win-x64 --self-contained` — a runtime-free build (no .NET prerequisite for users).
3. `vpk pack` — bundles it into the installer.

Output lands in `artifacts/releases/`:

| File | What it is |
|---|---|
| `QuackSnap-win-Setup.exe` | The installer users run. Adds Start Menu + Desktop shortcuts. |
| `QuackSnap-win-Portable.zip` | No-install version — unzip and run `QuackSnap.exe`. |
| `QuackSnap-*-full.nupkg`, `RELEASES`, `releases.win.json` | Update feed artifacts. |

## What the installer does

- Installs per-user to `%LocalAppData%\QuackSnap` (no admin prompt).
- Creates Start Menu and Desktop shortcuts using the QuackSnap icon
  (`windows/QuackSnap/Assets/QuackSnap.ico`).
- Registers in Add/Remove Programs for clean uninstall.
- "Start with Windows" stays a toggle inside the app's tray menu — the installer
  doesn't force autostart.

## Auto-updates

The app already calls `VelopackApp.Build().Run()` at startup, so it can update
itself. To enable it, host the contents of `artifacts/releases/` at a URL and add
an update check with `Velopack.UpdateManager` pointed at that feed. Publishing
a new version is just running the build script with a higher version and
uploading the new `artifacts/releases/` (Velopack computes deltas automatically).

## Code signing

Unsigned builds trigger SmartScreen on first run. To sign, pass Velopack's
signing parameters (an EV/OV code-signing cert is required) — see
`vpk pack --help` for `--signParams` / Azure Trusted Signing options. Signing
must run on Windows.
