# PRX Orbit identity

PRX Orbit is the visual identity for **RBG PaRuX AI Studio**. The approved mark
is fixed artwork: the metallic `PRX` monogram, two open blue/violet arcs and four
nodes must not be redrawn or geometrically reinterpreted.

## System

- **Primary app mark:** use the approved bitmap unchanged; cropping out the source
  caption and proportional resizing are the only permitted transformations.
- **Micro mark:** use the same approved artwork, reduced proportionally.
- **Shell mark:** the shared WinUI `PrxOrbitMarkTemplate` loads the approved asset;
  do not redraw one-off letter tiles in individual views.
- **Motion:** slow orbit for thinking, quicker orbit for work, one brief pulse for
  success/error, and a dimmed sleep treatment. Motion must remain lightweight.
- **Tone:** dark-first, technical and premium. Avoid extra gradients, glows,
  sparkle motifs, robot/brain imagery and high-frequency animation.

## Core palette

- PRX Core `#0A0F14`
- Orbit Blue `#2563EB`
- Orbit Light `#3B82F6`
- Routing Cyan `#22D3EE`
- Node Violet `#7C3AED`
- Success `#6CCB8F`; error `#FF9A8F` only for transient states

## Source assets

- `src/PaRuX.App/Assets/prx-orbit-approved.png` — canonical approved artwork.
- `src/PaRuX.App/Assets/prx-orbit-tray.ico` — dedicated Windows tray package.
- `tools/branding/generate-prx-assets.ps1` — reproducibly creates the multi-size
  Windows `.ico` and PNG preview by resizing the canonical artwork only.
- Android launcher artwork lives in `src/PaRuX.Android/app/src/main/res/drawable-nodpi`.
