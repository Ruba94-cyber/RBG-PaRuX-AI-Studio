# PaRuX AI & Gaming OS design system

Status: foundation specification, in progress. The application remains a Windows application; “OS” describes its integrated experience, not a replacement operating system. Existing product names, identifiers, data paths and installer identity must not be changed without a compatibility and migration review.

## Current inventory and scope

The Windows desktop uses WinUI. `src/PaRuX.App/Styles/Theme.xaml` defines theme dictionaries, semantic brushes, scales and styles, merged by the application. `App.xaml` also owns resources used by code lookup. `MainWindow.xaml` and its partial classes own shell navigation and responsive/accessibility behavior. This is a partial source audit, not a screen-by-screen usability or accessibility certification. Hardcoded values and existing visuals outside the shared resources remain to be inventoried and migrated incrementally. Android is a separate client and has not been audited in this cycle.

Existing raster/icon assets in `src/PaRuX.App/Assets/`: `parux.ico`, `prx-orbit-tray.ico`, `prx-orbit-preview.png`, and `prx-orbit-approved.png`. Their presence does not establish suitability for the new identity or every packaging surface. Preserve existing assets until replacements are reviewed at target sizes and installer, taskbar, tray and shortcut compatibility has been checked.

## Identity direction

Use the full display name **PaRuX AI & Gaming OS**, with **PaRuX** as the short name. Prefer a restrained geometric mark, neutral surfaces and a single cyan accent. Avoid purple, decorative gradients, heavy glow and literal gamepad/brain motifs. Maintain legibility and distinction at small sizes; do not imitate existing platform marks.

A reproducible starting construction for review, not an approved or produced logo: on a 64 × 64 square grid draw a single-color upright P using an outer path `M 14 8 H 37 C 49 8 54 15 54 25 C 54 35 49 42 37 42 H 26 V 56 H 14 Z`; subtract an inner opening `M 26 19 V 31 H 37 C 41 31 43 29 43 25 C 43 21 41 19 37 19 Z`. Inspect and adjust the silhouette, optical alignment and counters at 16 and 32 px before approval. This construction is a design proposal, not a trademark clearance or a claim that production vector/raster files exist.

Deliverables still required: reviewed editable vector master; light, dark, monochrome, compact icon and full lockup variants; raster exports at actual Windows sizes; tray, splash, installer and README applications. Verify contrast, silhouette and recognizable rendering against both themes and high contrast before replacing existing assets.

## Semantic colors

Target dark palette (hex sRGB):

| Role | Value |
| --- | --- |
| App background | `#0B0F14` |
| Primary surface | `#121820` |
| Secondary surface | `#1B242D` |
| Elevated surface | `#25313C` |
| Primary text | `#EDF2F5` |
| Secondary text | `#A5B2BD` |
| Primary accent | `#26B7C6` |
| Alternative accent | `#79CBD4` |
| Border | `#33404C` |
| Success | `#45B88D` |
| Warning | `#E2AD54` |
| Error | `#E16F72` |

Use semantic resources rather than embedding hex colors in views. Accent indicates primary action, selection or meaningful state; status must also be conveyed by text or icon. Choose foreground and background together: the palette alone does not prove a combination accessible. Test all actual text/state combinations against WCAG 2.2 AA, including disabled, hover, focus and selected states. Use Windows system colors for high contrast and verify live theme switching. Preserve a usable light theme with independently measured contrast; do not derive it mechanically from the dark palette. Translucency and Mica can change effective contrast and must be evaluated in the rendered app.

## Scale and components

Target spacing scale in effective pixels: 4, 8, 12, 16, 24, 32, 48, 64. Use 8–16 for compact control groups, 16–24 for cards and 24–32 for workspace sections; adapt to available width and text expansion. Recommended radii: 4 for small controls, 8 for cards, 12 for dialogs. Prefer Windows system typography (Segoe UI / Segoe UI Variable where available); suggested type sizes are 12 caption, 14 body, 16 emphasized body, 20 section and 28 page title. Use system font scaling and allow wrapping.

Interactive controls must retain visible keyboard focus, clear accessible names and sufficient pointer/touch target size. Use shared WinUI styles and theme resources, with normal, hover, pressed, disabled and focus states; do not replace control templates without verifying behavior. Sidebar, top bar, cards, dialogs, fields, buttons, grids, icons, shadows and animations need shared definitions as each is migrated. Keep motion subtle and respect Windows animation settings. Desktop supports mouse and keyboard; a future console shell must provide controller focus, distance-readable type and a virtual keyboard without duplicating application services.

## Verification and migration gates

1. Migrate shared tokens and a small set of shell/Home consumers while preserving existing resource keys and light/high-contrast behavior.
2. Build the affected Windows application; run relevant tests and inspect the actual app at multiple sizes, DPI scales and both languages. Record command, outcome and environment in `PARUX_EXECUTION_PLAN.md`.
3. Check navigation with keyboard and screen reader, focus visibility, text expansion, theme changes and measured contrast in actual rendered states.
4. Audit remaining Chat, models, agents, Computer Use, coding, media, gaming, settings, onboarding, downloads, notifications, dialogs and empty/loading/error states before claiming full coverage.
5. Treat physical controller, TV/display, streaming and gaming performance as separate acceptance gates. No screen-wide WCAG compliance, logo approval or physical acceptance is claimed by this document.
