# Edge TTS Overlay — Desktop Design System

## Product and material

This is a compact Windows productivity overlay, not a web page. The always-on-top capsule is one
rounded surface drawn with WPF per-pixel alpha (`AllowsTransparency="True"`), so the desktop behind
it shows through directly. Content sits fully opaque on top of it.

Because there is no backdrop blur to lean on (see below), the glass is built from stacked layers
inside a single `Border`. Keep all of them — dropping one makes the capsule read as a flat dark slab:

| Layer | What it does |
|---|---|
| Surface gradient | Diagonal tint, clearest at the top-left, densest at the bottom-right |
| Glass core | Same tint inset 6 DIP, so the outer ring stays the most see-through part and the middle stays readable |
| Rim (1.5 DIP stroke) | Specular edge: bright at top-left and bottom-right, dim along the sides |
| Bevel (1 DIP, inset) | Dark hairline just inside the rim. Two lines side by side is what reads as thickness; a white rim alone disappears on a bright desktop |
| Top / floor sheen | Incoming light on the top edge, internal bounce on the bottom edge |
| Specular streak | One 1.5 DIP highlight under the top edge |
| Shadow | One hollow `Border` ring on the capsule edge carrying a blurred `DropShadowEffect`; only the blur escapes outward |

The shadow caster is a **hollow** ring, not a filled shape: a filled caster's shadow sits under the
translucent surface and darkens the whole capsule. Its sharp 3 DIP stroke lands exactly on the
capsule edge, so only the blurred part escapes into the window's 18 DIP gutter — which is why the
window is larger than the glass on every side. The rim and bevel hide the stroke geometrically but
not optically: both are translucent, so the near-black caster reads through them and takes some
brightness off the specular edge. The rim values below were picked with it already in place, so
changing the caster's alpha means re-checking the rim on a real screenshot, not just the offscreen
render — `check_overlay_visuals.ps1` samples the capsule centre and the halo, never the rim itself.

Do not go back to stacking a few solid rings to fake the blur. That was tried first and the steps
between rings read as a second, hard-edged frame floating around the capsule. A real blur is the
only version that reads as a shadow. It does cost something: layered windows rasterise in software,
and an offscreen render of one expand animation measured 3.8 ms/frame without the effect and
8.6 ms/frame with it (`RenderingBias="Performance"`, blur 12). That is still inside a 60 fps budget,
and it is only paid while the window is resizing — at rest WPF caches the rendering. If the blur
radius grows much beyond this, re-measure.

### Why there is no real backdrop blur

Tested on Windows 11 26200 (2026-09-14): `SetWindowCompositionAttribute` with
`ACCENT_ENABLE_ACRYLICBLURBEHIND` on this window changes nothing — a layered (per-pixel alpha)
window does not receive the DWM accent. The documented Win11 path (`DWMWA_SYSTEMBACKDROP_TYPE`)
needs `AllowsTransparency="False"`, which costs the anti-aliased capsule silhouette and the soft
shadow, and caps the corner radius at the system's ~8 DIP. Both trades are worse than no blur, so
do not re-add a DWM backdrop, system frame or custom HRGN; each one paints a second, differently
rounded surface and brings back the double-border look.

The consequence to design around: transparency shows the desktop at full sharpness, so legibility
comes from the glass core, from panels that darken rather than lighten, and from a soft dark halo on
the compact row's text.

## Tokens

| Role | Value |
|---|---|
| Surface tint | `#6E2C4272` → `#7C15274F` → `#8A0B1631` (diagonal) |
| Glass core | `#33070E1C` → `#3D060C18`, inset 6 DIP |
| Rim | white `#8C` → `#33` → `#14` → `#2B` → `#5E`, 1.5 DIP |
| Bevel | `#2E000814`, 1 DIP inset |
| Shadow | caster `#A6000512` 3 DIP hollow ring; blur 12, depth 3, opacity 0.8 |
| Primary text | `#F3F7FF` |
| Secondary text | `#C2CFE4` |
| Muted text | `#A7B4CB` |
| Accent | `#8FB7FF`; progress fill `#7FD0FF` → `#A9B4FF` |
| Panel / well | `#2B0A1220` / `#38060C18` with white `#26` / `#1F` rims |
| Control surface | `#16FFFFFF`; hover `#2EFFFFFF`; pressed `#548FB7FF`; primary `#528FB7FF` |
| Corner radius | 26 capsule · 20 core · 18 panels · 15 buttons · 12 list rows |
| Spacing | 4 / 8 / 14 / 18 px |
| Typeface | Segoe UI Variable Text; Segoe Fluent Icons |

Panels on the glass darken (`#2B0A1220`) instead of lightening. A white overlay looks right on a dark
desktop and leaves white text with nothing to stand on over a bright one.

## Interaction

- Window 436 × 112 DIP resting, 436 × 446 expanded — 18 DIP of that is the shadow gutter on each
  side, so the glass itself is 400 × 76 and 400 × 410. `Window.Background` is `{x:Null}` so the
  gutter does not swallow clicks meant for the desktop.
- It expands only on click, in 240 ms with ease-out; the chevron rotates 180° and the detail pane
  fades in on the same curve. It repositions upward to stay inside the work area.
- No idle animation. Buttons squish to 0.9 on press and spring back with a `BackEase` — that bounce
  is the only "liquid" motion in the product.
- The overlay is draggable, clickable, topmost, and uses `WS_EX_NOACTIVATE` plus `MA_NOACTIVATE`.
  Clicking it, or opening the voice dropdown, must not change the foreground window. Only owned
  editor/settings windows accept keyboard focus.
- Every icon button has a tooltip and `AutomationProperties.Name`; hit targets are at least 44 × 44
  DIP (the speed stepper's −/＋ are 42 inside a 44 pill).
- The surface is decorative. Text and icons stay opaque so contrast never depends on what happens to
  be behind the window. Content and state do not rely on transparency or color alone.

## Layout

The compact row carries play/pause, a two-line status (state, then the current sentence — or the
read-clipboard hotkey when nothing is playing), skip, stop, expand, and a 3 DIP progress bar along
the bottom edge showing position within the article.

Expanded adds, in order: the current segment card with an `n / total` counter; a speed stepper and a
voice picker (the two settings people change while listening — reaching them must not open the modal,
which pauses playback); the article queue; one labelled action plus five icon actions, where the
selection-dependent ones disable when nothing is selected; and a line listing the live hotkeys.
Long original text belongs in the focused editor window, where original and spoken preview stay
visible together.

Empty states say what to press, not just that the list is empty.

## Settings and editor windows

They are ordinary opaque windows — they need a title bar to move and close — and only borrow the
palette: `DialogSurface` gradient, 18 DIP section cards, the same inputs and buttons.
`DarkTitleBar.Apply` is required on both; without it Windows 11 puts a white title bar on dark
content.

## Gotchas

- A `ComboBox` using this template **must** set `ItemTemplate`. With only `DisplayMemberPath`,
  `SelectionBoxItemTemplate` stays null (verified 2026-09-14) and the closed box prints the item's
  `ToString()`.
- `Border.CornerRadius` does not clip children. Rounded inner layers each carry their own matching
  radius instead of relying on a parent clip.
- A multi-line `TextBox` needs `VerticalContentAlignment="Top"`; the shared template defaults to
  `Center` so single-line fields sit right, and a multi-line box left at the default parks its text
  in the middle of the box.

## Avoid

- Marketing-page sections, hero layouts, testimonials, gold CTA colors, hospitality styling, or web fonts.
- Hover-only disclosure, mouse passthrough, continuous motion, hidden focus in editor/settings, or
  letting text contrast depend on the desktop showing through.
