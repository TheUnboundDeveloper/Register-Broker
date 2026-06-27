# Documentation images

Diagrams and screenshots embedded across the repo's markdown. Each architecture
diagram is authored as an **`.svg`** (the source of truth, rendered repo-relative in
the README and docs) with a matching **`.png`** rendered alongside at 2× for contexts
that need a raster (e.g. the GitHub wiki, which references the PNG by absolute URL).
The screenshot is the first-party **Reference Console**
([`Test_GUI/ReferenceConsole/`](../../Test_GUI/ReferenceConsole/)) driving the
framework as a non-admin client over the broker pipes.

To re-render the PNGs after editing an SVG, screenshot each with headless Chrome at the
SVG's `viewBox` size (`--headless=new --force-device-scale-factor=2 --screenshot=…
--window-size=W,H file:///…svg`).

| File (`.svg` + `.png`) | Embedded in | Shows |
|---|---|---|
| `architecture-overview` | `README.md`, `docs/ARCHITECTURE.md` | Full stack overview — non-admin clients → authenticated named pipes → broker (control plane, registries, driver/user-mode/USB-HID backends) → signed kernel driver → hardware. Distinguishes the kernel-mediated (brick-guarded) path from the opt-in user-mode vendor paths |
| `architecture-layers` | `docs/ARCHITECTURE.md` §2 | The four layers (consumers · broker · kernel driver · hardware) with the named-pipe privilege boundary called out |
| `trust-boundary` | `docs/ARCHITECTURE.md` §4 | Untrusted non-admin side vs. the elevated/LocalSystem side, divided by the authenticated pipe and its gate steps |
| `request-flow` | `docs/ARCHITECTURE.md` §5 | Sequence diagram of `sensor.read cpu.temp` from client → broker → driver → hardware and back |
| `three-pieces` | `docs/README.md` | The three pieces (client · broker service · kernel driver) as a top-down flow to the hardware |
| `wiki-pieces` | `wiki/Architecture-and-Security.md` | The client → broker → driver pieces, referenced as a PNG by absolute raw URL from the wiki |

| Screenshot | Shows |
|---|---|
| `reference-console-dashboard.png` | Dashboard — live `sensor.readall` (69 sensors) alongside the RGB fleet (DRAM, MSI ARGB header, two Razer devices), driven non-admin over the broker pipes |
