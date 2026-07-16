# QuackSnap

AirDrop-style screenshot transfer from Windows to iPhone. Take a screenshot on
Windows (Win+Shift+S, PrintScreen, Snipping Tool) and it appears on the paired
device with zero clicks.

## Layout

```
windows/QuackSnap/            Windows tray app (.NET 8, WinForms shell)
windows/QuackSnap.Protocol/   Shared wire protocol: framing, pairing, cert pinning
ios/QuackSnapKit/             Swift package: same protocol + TLS receiver (iOS & macOS)
ios/QuackSnap/                SwiftUI iPhone app (pairing, gallery, notifications)
relay/                        Cloudflare Worker + docs for background delivery
web/                          Marketing landing page (static HTML/CSS/JS)
tools/TestReceiver/           Cross-platform .NET dev receiver
tools/DevRelay/               Local relay that pushes into the iOS simulator
```

## Brand

QuackSnap has one visual identity across the app and site: a duck-head mark on a
warm amber→coral gradient (`#FFC53D` → `#FF6B3D`), Nunito for display type, and a
consistent set of file-type accent colors. The iOS app (`ios/QuackSnap/QuackSnap/
Views/Theme.swift`) and the landing page (`web/styles.css`) share the same
tokens, so the "Live" status pill, colored file tiles, and gradient CTAs look the
same in both places. Everything is dark-mode aware.

## Windows app

Requires the .NET 8 SDK.

```sh
dotnet build QuackSnap.sln           # compiles everywhere (EnableWindowsTargeting)
cd windows/QuackSnap && dotnet run   # runs on Windows only
```

### Installer

`./windows/build-installer.sh` builds a self-contained **`Setup.exe`** (plus a
portable zip) with Velopack — and it cross-compiles, so the installer can be
produced on macOS/Linux too. Output goes to `artifacts/releases/`. See
[windows/INSTALLER.md](windows/INSTALLER.md) for details, code signing, and
auto-updates.

## iPhone app

Open `ios/QuackSnap/QuackSnap.xcodeproj` in Xcode, set your signing team, and
run on a device or simulator. The app links the local `QuackSnapKit` package.

Pairing flow (LocalSend-style): Windows tray → **Pair a device → Pair
application…** shows a **6-digit code** and advertises itself over Bonjour.
Type the code in the app — the phone finds the PC automatically. Scanning the
QR code, pasting the full `quacksnap://` code, or tapping a `quacksnap://` link
(deep-link pairing) all work too when discovery is blocked. Wrong codes are rejected (HMAC) and three failures kill the pairing
session. iOS will prompt for Local Network access on first pairing — that
permission is required.

Every received screenshot is saved to the gallery, **copied straight to the
iPhone clipboard** (screenshot on Windows → paste on iPhone, zero taps), and
posted as a notification with an image preview if the app is backgrounded.

**Copied text transfers too.** Enable **Send copied text** in the Windows tray,
and highlighted text you copy (Ctrl+C) is sent as a `.txt` — it shows in the
gallery and lands on the iPhone clipboard, ready to paste. Off by default so not
every Ctrl+C is transferred.

**Any file type can be sent** — drop PDFs, documents, video, zips, anything
onto the Windows drop window. Images show as thumbnails on the phone; other
files get a document tile and open in QuickLook (share/save from there).

**iPhone → Windows works too.** Tap the paperplane in the app to send photos or
files back to your PC. The phone discovers the paired computer on the network
(`_quacksnap._tcp`), connects over the same mutually-authenticated TLS, and the
files land in `Pictures\QuackSnap` with a tray notification. Same protocol, same
pinning — just the other direction.

### Dev receiver without a phone

`quacksnap-cli` in `ios/QuackSnapKit` exercises the exact receive path on
macOS, and `tools/TestReceiver` is the .NET equivalent:

```sh
cd ios/QuackSnapKit && swift build
.build/debug/quacksnap-cli pair "quacksnap://pair?..."
.build/debug/quacksnap-cli listen        # saves into ./received-swift
```

## How it works

- **Capture (Windows)** — `AddClipboardFormatListener` (event-driven, no
  polling) plus a `FileSystemWatcher` on `Pictures\Screenshots`. Clipboard-owner
  heuristics decide what counts as a screenshot; decoded-pixel hashing dedupes
  captures that arrive via both routes.
- **Queue** — disk-backed (`%APPDATA%\QuackSnap\queue`), survives restarts,
  exponential backoff, flushes immediately when the peer comes back online.
- **Transport** — mutually authenticated TLS over TCP. Both sides use
  self-signed ECDSA P-256 certs and validate exactly one thing: the SHA-256
  fingerprint pinned at pairing time. Transfers are chunked (256 KB), resumable
  by offset, and acknowledged only after the receiver verifies the SHA-256 of
  the whole file. The Swift and .NET implementations are wire-compatible
  (verified against each other, including mid-transfer resume).
- **Pairing** — an out-of-band secret (128-bit in the QR/URI, or the 6-digit
  typed code) is proven via HMAC on both sides before cert fingerprints are
  exchanged. The pairing listener exists only while the pairing window is open,
  advertises via DNS-SD (`_quacksnap-pair._tcp`) for discovery, and shuts down
  after three failed attempts to offset the short code's low entropy.
- **Background delivery** — when the phone isn't on the LAN, the Windows app
  seals the file to the phone's certificate (AES-256-GCM, key wrapped with
  ECDH-ES + HKDF, sender-signed), uploads the ciphertext to the relay, and asks
  it to push. The phone's Notification Service Extension pulls the ciphertext and
  decrypts it in the shared app group, so the file appears on the lock screen
  without the app running. Relay and Apple see only ciphertext. See `relay/`.
- **Transport seam** — `ITransport` on the Windows side is where the future
  **website** receiver plugs in; devices already carry a
  `kind: application | web` field.

## Settings

- Windows tray: auto-send screenshots, send all copied images, compress
  screenshots (JPEG), relay URL, start with Windows.
- iPhone: copy images to clipboard, save images to Photos, unpair.

## Current limitations

- Background delivery needs a deployed relay (`relay/`) and, on device, real APNs
  registration. The `simctl push` path exercises everything but the OS
  auto-launch of the extension, which only happens for real pushes on device.
- Reconnects depend on the phone keeping its IP; pairing uses Bonjour
  discovery already, but transfer reconnects don't yet.
- mDNS advertising on Windows (Makaretu.Dns) shares UDP 5353 with the OS
  resolver — verified on macOS, needs a smoke test on real Windows.

## Roadmap

- Website receiver (`web/`) — browser-based receiving behind the same queue
- Bonjour discovery for transfer reconnects (pairing already uses it)
- Live Activity / lock-screen widget for the most recent file
- Packaging: MSIX + code signing (Windows), TestFlight + App Store (iOS)
