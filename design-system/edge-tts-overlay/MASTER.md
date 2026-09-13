# Edge TTS Overlay — Desktop Design System

## Product and material

This is a compact Windows productivity overlay, not a web page. The always-on-top capsule is one rounded surface drawn with WPF per-pixel alpha (`AllowsTransparency="True"`), so the desktop behind it shows through directly. This is straight transparency: no blur, no optical refraction, and no opaque fallback. A single tinted surface owns the whole silhouette and content sits fully opaque on top of it. Do not add a DWM backdrop, system frame or custom HRGN behind it — each one paints a second, differently rounded surface and brings back the double-border look.

## Tokens

| Role | Value |
|---|---|
| Surface tint | `#A6182232` (65% alpha) |
| Surface border | `#38FFFFFF` |
| Primary text | `#F8FAFF` |
| Secondary text | `#C9D5E8` |
| Accent | `#78A9FF` |
| Control surface | `#10FFFFFF`; hover `#26FFFFFF` |
| Pressed surface | `#5278A9FF` |
| Corner radius | 24 DIP capsule; 16 DIP expanded groups |
| Spacing | 4 / 8 / 12 / 16 / 24 px |
| Typeface | Segoe UI Variable Text; Segoe Fluent Icons |

## Interaction

- Resting state is a 360×76 DIP capsule. It expands only on click, in 180 ms with ease-out, and repositions upward to remain inside the work area.
- No idle animation. Pressed and hover states change material color without shifting layout.
- The overlay is draggable, clickable, topmost, and uses `WS_EX_NOACTIVATE` plus `MA_NOACTIVATE`. Only owned editor/settings windows accept keyboard focus.
- Every icon button has a tooltip and `AutomationProperties.Name`; hit targets are at least 44×44 DIP.
- The surface is decorative. Text and icons stay opaque so contrast never depends on what happens to be behind the window. Content and state do not rely on transparency or color alone.

## Layout

The compact row contains pause, two-line status/current sentence, stop, and expand. Expanded content shows the full current segment, a compact article queue, and labeled actions. Long original text belongs in the focused editor window, where original and actual spoken preview remain visible together.

## Avoid

- Marketing-page sections, hero layouts, testimonials, gold CTA colors, hospitality styling, or web fonts.
- Hover-only disclosure, mouse passthrough, continuous motion, hidden focus in editor/settings, or letting text contrast depend on the desktop showing through.
