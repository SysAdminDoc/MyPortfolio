# MyPortfolio — logo generation prompts

5 prompts covering the standard variants. Every prompt below is **non-negotiable**:

- **Transparent background** — alpha=0 outside the glyph. No white fill, no dark fill, no full-canvas rectangle. The icon must composite cleanly onto any host surface.
- **PNG with alpha channel (RGBA)** — no JPEG. If the tool returns a flattened image, regenerate or post-process with background removal before shipping.
- **High contrast** — must read on both light and dark surfaces.
- **SVG-friendly geometry** — clean shapes, no gradients that break at small sizes.
- **No text** unless the prompt is explicitly the Wordmark variant.

**Project context (use in every prompt):**
> MyPortfolio is a single Windows desktop catalog app that combines three curated stores —
> Windows desktop installers, Chromium browser extensions, and Android APKs — into one
> tabbed shell driven by GitHub releases. Visual identity should suggest unification of
> three distinct surfaces under one roof, not the surfaces themselves.

---

## 1. Minimal icon — flat single-color glyph

> Flat single-color glyph for a 16-128 px Windows toolbar icon. Three slim vertical
> rectangles of equal width, lightly overlapping at their bottom edge to suggest a
> stacked file-browser tab strip — left rectangle in Catppuccin mauve (#cba6f7),
> middle in Catppuccin sapphire (#74c7ec), right in Catppuccin teal (#94e2d5). No
> outlines, no shadows, no text. Transparent background (alpha=0). Output: PNG with
> alpha channel, 1024x1024, RGBA. Geometry must read clearly when downscaled to 16 px.

## 2. App icon — rounded square with depth

> Rounded-square app icon (Windows / Chrome Web Store / Android adaptive sizing). Three
> overlapping vertical "tab" panels rendered with subtle layered depth, top-down
> illumination from the upper-left, slight 1 px highlight on each tab's leading edge.
> Tab colors in Catppuccin order: mauve (#cba6f7), sapphire (#74c7ec), teal (#94e2d5).
> The glyph occupies ~80% of the canvas with comfortable padding. NO surrounding tile,
> NO frame, NO base color — the rounded square IS the glyph silhouette. Transparent
> background outside the silhouette (alpha=0). Output: PNG with alpha channel, 1024x1024,
> RGBA, recognizable at 48 px.

## 3. Wordmark — typography-focused

> Wordmark for "MyPortfolio". A custom geometric sans-serif lockup, the "M" of
> "My" stylized as three abutting vertical strokes that visually echo the three-tab
> motif (mauve / sapphire / teal Catppuccin accents on the three strokes; the
> remaining letters in Catppuccin text grey #cdd6f4). Tight letter spacing, all
> letters opaque glyphs only — the BACKGROUND outside the glyphs is transparent
> (alpha=0). No bounding box, no underline, no tagline. Output: PNG with alpha
> channel, 2048x512, RGBA, suitable for a README banner.

## 4. Emblem — badge / shield / crest

> Emblem for a README header — a soft hexagonal badge silhouette with three
> concentric chevrons inside (mauve outer chevron, sapphire middle, teal inner)
> nested into a "tab strip" reading. Subtle 1 px inner stroke at #45475a. NO
> surrounding glow, NO drop shadow, NO outer rectangle. The hexagon IS the glyph;
> outside the hexagon is fully transparent (alpha=0). Output: PNG with alpha
> channel, 1024x1024, RGBA, recognizable as a portfolio mark when shown at 256 px.

## 5. Abstract — conceptual

> Abstract conceptual mark — three tilted rounded rectangles arranged like fanned
> playing cards, each rotated 8 degrees from the next, ascending size left-to-right.
> Smallest card in Catppuccin teal (#94e2d5, "Android"), middle in sapphire
> (#74c7ec, "Chrome"), largest in mauve (#cba6f7, "Desktop"). Each card has a
> subtle 1 px inner stroke for definition. Suggests a portfolio of layered surfaces.
> No symbols, no glyphs, no text on the cards. Transparent background outside the
> cards (alpha=0). Output: PNG with alpha channel, 1024x1024, RGBA.

---

## After the logo is generated

Per global Branding rule, integrate in one pass:

- [x] Repo-root `icon.png` + `icon.svg`
- [x] Size variants 16 / 32 / 48 / 64 / 128 / 256 / 512 / 1024 (PNG, RGBA)
- [x] Repo-root `icon.ico` (Windows multi-res — wire into `<ApplicationIcon>` in csproj)
- [x] Repo-root `banner.png` (the Wordmark variant, 2048×512)
- [x] Repo-root `logo.png` (the App-icon variant, 512×512)
- [x] README centered header — `banner.png` + title-line w/ `logo.png`
- [x] Replace the in-app `MP` text placeholder in `MainWindow.xaml` with the actual icon

Implemented from deterministic SVG sources:

- `icon.svg`
- `branding/banner.svg`
- `branding/icons/icon-16.png` through `branding/icons/icon-1024.png`
- `src/MyPortfolio/Assets/icon.ico` and `src/MyPortfolio/Assets/logo.png` for the WPF shell

**Verify every PNG has an alpha channel:**

```bash
magick identify -format '%[channels]' <file>.png
```

Must return `rgba` / `srgba` / `graya`. A flat `rgb` return means transparency was
lost in the pipeline — regenerate before committing.
