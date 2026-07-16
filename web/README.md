# QuackSnap landing page

Static marketing site — no build step, no dependencies. Plain HTML/CSS/JS that
shares QuackSnap's brand tokens with the iOS app.

```sh
python3 -m http.server 8123 --directory web
# open http://localhost:8123
```

Files:

- `index.html` — hero (two-way visual), "what you can send" strip, features,
  how-it-works, security, FAQ, download, footer. Includes OG/Twitter meta and
  `SoftwareApplication` JSON-LD.
- `styles.css` — brand tokens (amber→coral gradient, Nunito display), responsive
  grid, mobile nav, FAQ accordion, dark-mode via `prefers-color-scheme`
- `app.js` — footer year, sticky-nav hairline, mobile-nav toggle, reveal-on-scroll
  (respects `prefers-reduced-motion`)
- `favicon.svg`, `assets/og.png` — the snip+spark mark; regenerate the OG card
  with `gen_og.swift`

Fonts load from Google Fonts; everything else is self-contained. The snip+spark
mark is an inline SVG `<symbol>` reused everywhere. Copy is kept in sync with the
app (two-way transfer, any file, copied text, E2EE). Not to be confused with the
planned **web receiver** (browser-based file receiving) still on the roadmap.

Deploy note: the OG/canonical URLs use a placeholder `https://quacksnap.app` —
swap in the real domain before publishing.
