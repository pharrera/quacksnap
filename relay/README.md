# QuackSnap relay

End-to-end-encrypted store-and-forward for background delivery. When the phone
isn't reachable on the LAN, the Windows app seals the file to the phone's device
certificate, uploads the ciphertext here, and asks the relay to send a push. The
phone's Notification Service Extension pulls the ciphertext and decrypts it — so
the screenshot appears on the lock screen without the app running.

**The relay and Apple only ever see ciphertext.** Blobs are AES-256-GCM sealed to
the recipient (ECDH-ES + HKDF), and the push payload carries only the signed
envelope. See `windows/QuackSnap.Protocol/Envelope.cs` and
`ios/QuackSnapKit/Sources/QuackSnapKit/Envelope.swift`.

## HTTP API

```
PUT  /v1/blob/{id}     store ciphertext (10-minute TTL)
GET  /v1/blob/{id}     fetch ciphertext
POST /v1/register      { deviceId, token }  → map device to its APNs token
POST /v1/push          { to, payload }       → forward payload to APNs
```

## Production (Cloudflare)

`worker.js` + `wrangler.toml`. Cheapest real option: R2 for blobs (add a
10-minute lifecycle rule), KV for device→token, APNs token auth via a `.p8` key.

```sh
wrangler kv namespace create TOKENS      # paste the id into wrangler.toml
wrangler r2 bucket create quacksnap-blobs
wrangler secret put APNS_TEAM_ID
wrangler secret put APNS_KEY_ID
wrangler secret put APNS_PRIVATE_KEY     # contents of the .p8
wrangler secret put APNS_TOPIC           # com.peterherrera.quacksnap
wrangler deploy
```

Then set the deployed URL in the Windows tray (**Set relay URL…**); it's handed
to the phone at pairing time.

## Local development

`tools/DevRelay` is the same HTTP API, but instead of APNs it injects the push
into a booted iOS Simulator with `xcrun simctl push`:

```sh
dotnet run --project tools/DevRelay -- --sim booted
```

Note: `simctl push` delivers the notification but does **not** auto-launch the
Notification Service Extension (a simulator limitation — NSEs only auto-run for
real APNs pushes on device). The NSE's own logic (relay fetch + verify +
decrypt) is exercised by `quacksnap-cli nse-receive` against the live relay.
