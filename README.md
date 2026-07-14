# ScreenDrop

AirDrop-style screenshot transfer from Windows to a paired device. Take a
screenshot on Windows (Win+Shift+S, PrintScreen, Snipping Tool) and it appears
on the paired device with zero clicks.

## Layout

```
windows/ScreenDrop/           Windows tray app (.NET 8, WinForms shell)
windows/ScreenDrop.Protocol/  Shared wire protocol: framing, pairing, cert pinning
tools/TestReceiver/           Cross-platform dev receiver (stands in for the iPhone app)
```

## Building

Requires the .NET 8 SDK.

```sh
dotnet build ScreenDrop.sln          # compiles everywhere (EnableWindowsTargeting)
```

The tray app itself only *runs* on Windows:

```sh
cd windows/ScreenDrop
dotnet run                           # on a Windows machine
dotnet publish -c Release            # distributable build
```

## Trying it end to end (any OS pair)

1. On Windows, run ScreenDrop → tray icon → **Pair a device → Pair application…**
2. Copy the `screendrop://pair?...` code from the pairing window.
3. On the receiving machine:
   ```sh
   cd tools/TestReceiver
   dotnet run -- pair "screendrop://pair?..."
   dotnet run -- listen
   ```
4. Take a screenshot on Windows. It lands in `tools/TestReceiver/received/`.

Both machines must be on the same network, and Windows Firewall must allow the
receiver's inbound port (47820) on the receiving side.

## How it works

- **Capture** — `AddClipboardFormatListener` (event-driven, no polling) plus a
  `FileSystemWatcher` on `Pictures\Screenshots`. Clipboard-owner heuristics
  decide what counts as a screenshot; decoded-pixel hashing dedupes captures
  that arrive via both routes.
- **Queue** — disk-backed (`%APPDATA%\ScreenDrop\queue`), survives restarts,
  exponential backoff, flushes immediately when the peer comes back online.
- **Transport** — mutually authenticated TLS over TCP. Both sides use
  self-signed ECDSA P-256 certs and validate exactly one thing: the SHA-256
  fingerprint pinned at pairing time. Transfers are chunked (256 KB), resumable
  by offset, and acknowledged only after the receiver verifies the SHA-256 of
  the whole file.
- **Pairing** — QR/URI carries a one-time secret out of band; both sides prove
  knowledge of it via HMAC before exchanging cert fingerprints. The pairing
  listener only exists while the pairing window is open.
- **Transport seam** — `ITransport` (`Transfer/ITransport.cs`) is where the
  future **website** receiver plugs in; devices already carry a
  `kind: application | web` field.

## Roadmap

- iOS app (`ios/`) — same protocol; relay + APNs path for background delivery
- Website receiver (`web/`) — browser-based receiving behind the same queue
- mDNS/Bonjour discovery so reconnects don't depend on stable IPs
- Packaging: MSIX, code signing, auto-update
# quacksnap
