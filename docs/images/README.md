# Documentation images

Images embedded in the top-level `README.md`. The screenshots are the first-party
**Reference Console** ([`Test_GUI/ReferenceConsole/`](../../Test_GUI/ReferenceConsole/))
driving the framework as a non-admin client over the broker pipes.

| File | Shows |
|---|---|
| `architecture-overview.svg` | Layered architecture diagram — non-admin clients → authenticated named pipes → broker (control plane, registries, driver/user-mode/USB-HID backends) → signed kernel driver → hardware. Distinguishes the kernel-mediated (brick-guarded) path from the opt-in user-mode vendor paths |
| `reference-console-dashboard.png` | Dashboard — live `sensor.readall` (69 sensors) alongside the RGB fleet (DRAM, MSI ARGB header, two Razer devices), driven non-admin over the broker pipes |
