namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| RgbZone / RgbBoardProfile — the board-aware RGB hardware map                 |
|                                                                            |
|   The RGB analogue of the sensor ChannelRegistry/Calibration split: which   |
|   RGB zones a board has, on which TRANSPORT, and at which address/register   |
|   are SIGNED CODE (here), never data — the same rule as register maps        |
|   (calibration JSON may only relabel/hide a zone by id, never address it).   |
|                                                                            |
|   A zone names a logical RGB device a client can drive (rgb.list / rgb.set). |
|   Three transports coexist behind IRgbController:                            |
|     * SmbusEne   — ENE/Aura DRAM over the kernel SMBus write path (validated).|
|     * SuperioEc  — NCT6687 motherboard-header RGB over the kernel EC write    |
|                    path. HW-UNVALIDATED: the EC RGB register window is not    |
|                    yet confirmed, so the driver keeps this path inert         |
|                    (CAP_SUPERIO_RGB off) — the zone simply never instantiates |
|                    until the kernel advertises the capability.               |
|     * UsbHid     — MSI Mystic Light over USB-HID (the working motherboard     |
|                    path; opt-in, gated by AllowHidRgb).                       |
|                                                                            |
|   DRIVER-STABILITY CONTRACT: adding a board/zone is a broker-only change.     |
|   The per-board addresses live here; the kernel only exposes stable,         |
|   class-wide windows (the SMBus RGB range / the NCT6687 RGB register region). |
\*---------------------------------------------------------------------------*/
internal enum RgbTransport
{
    SmbusEne,    // ENE/Aura DRAM over SMBus (kernel brick-guarded write)
    SuperioEc,   // NCT6687 EC register RGB (kernel brick-guarded EC write)
    UsbHid,      // MSI Mystic Light over USB-HID (user-mode, broker-only)
    UsbHidRazer  // Razer Chroma peripherals over USB-HID (user-mode, board-independent)
}

/// <summary>The logical capability classes every board profile is expected to cover where the
/// hardware exists (the "same features on each board" parity vocabulary).</summary>
internal enum RgbZoneKind
{
    Dram,        // addressable DRAM module LEDs
    Mb12V,       // 12V non-addressable motherboard header (JRGB) — a single solid-color zone
    MbArgb,      // 5V addressable motherboard header (JRAINBOW / JARGB) — per-LED
    Keyboard,    // USB-HID keyboard peripheral (not a board zone; not part of board parity)
    Mouse        // USB-HID mouse peripheral (not a board zone; not part of board parity)
}

/// <summary>
/// One drivable RGB zone in a board profile. Only the fields relevant to <see cref="Transport"/>
/// are meaningful (Bus/Address for SmbusEne; EcAddress for SuperioEc; HidZoneIndex/HidLedOffset/
/// HidProductId for UsbHid). For UsbHid, <see cref="HidZoneIndex"/> is the zone's BYTE OFFSET in the
/// Mystic Light 185-byte sync packet (e.g. 31 = j_rainbow_1 in FeaturePacket_185), not an ordinal.
/// Addresses/registers are SIGNED CODE and never leave the broker — clients name <see cref="Id"/>.
///
/// <para>An addressable header (<see cref="RgbZoneKind.MbArgb"/>) is driven PER-LED in direct mode
/// (report 0x53): RGB is written literally per LED, so brightness is linear (no firmware sync engine),
/// unlike the 185-byte sync path which folds. Each addressable zone is its OWN per-LED frame, selected
/// by two header bytes — <see cref="HidPerLedHdr1"/> / <see cref="HidPerLedHdr2"/> — not by a flat
/// offset (JRAINBOW1 = 4/0, JRAINBOW2 = 4/1, JCORSAIR = 5/0; the zone's LEDs start at index 0 of its
/// own frame). These are the public-protocol per-zone selectors; the board's value is confirmed at
/// bring-up (the <c>--mystic-perled</c> dev probe). Meaningful only for UsbHid MbArgb zones.</para>
///
/// <para><see cref="HidProductId"/> PINS a UsbHid zone to exactly one USB product id under the MSI
/// vendor id (0x1462), so the broker drives only the intended Mystic Light controller and refuses
/// any other MSI HID interface. 0 means "unpinned" — match by feature-report length (bring-up
/// mode; the candidate PIDs are logged so the profile can be pinned).</para>
/// </summary>
internal sealed record RgbZone(
    string Id,
    string Label,
    RgbZoneKind Kind,
    RgbTransport Transport,
    int LedCount,
    int Bus = 0,
    int Address = 0,
    int EcAddress = 0,
    int HidZoneIndex = 0,
    int HidPerLedHdr1 = 0,
    int HidPerLedHdr2 = 0,
    int HidProductId = 0);

/// <summary>
/// A board's RGB hardware map, selected by DMI identity. The generic profile (empty
/// manufacturer/product = wildcard) is the fallback used on unrecognised boards.
/// </summary>
internal sealed record RgbBoardProfile(string Manufacturer, string Product, IReadOnlyList<RgbZone> Zones)
{
    public bool IsGeneric => string.IsNullOrEmpty(Manufacturer) && string.IsNullOrEmpty(Product);
}
