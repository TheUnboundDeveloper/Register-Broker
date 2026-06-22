namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| RgbCatalog                                                                 |
|                                                                            |
|   The control service's BAKED, board-aware map of RGB zones to their        |
|   transport + physical address/register. Clients name a logical zone        |
|   ("ram0", "mb.argb0") and a color; they never supply or discover an        |
|   address (the write-safety rule). The kernel brick-guards each write path  |
|   independently, so even this map cannot reach SPD or the EC sensor banks.  |
|                                                                            |
|   Board profiles are selected by DMI identity (same matcher as sensor       |
|   calibration). Adding a board is a BROKER-ONLY change: a new profile row    |
|   here, no driver recompile, because the kernel exposes only stable          |
|   class-wide windows (see RgbZone.cs, the driver-stability contract).        |
|                                                                            |
|   Dev box (MSI B550I + G.Skill DDR4): ENE/Aura DRAM at bus 0, 0x39/0x3A;     |
|   motherboard headers via NCT6687 EC (inert until HW-validated) and the      |
|   USB-HID Mystic Light path.                                                |
\*---------------------------------------------------------------------------*/
internal static class RgbCatalog
{
    /// <summary>The capability vocabulary every non-generic profile is expected to cover where the
    /// hardware exists (drives the parity report — "same features on each board").</summary>
    public static readonly IReadOnlyList<RgbZoneKind> StandardKinds =
        new[] { RgbZoneKind.Dram, RgbZoneKind.Mb12V, RgbZoneKind.MbArgb };

    /*-- Broker-side MIRROR of the kernel write windows (BrokerSmbusDriver/inc/SmbusBrokerProtocol.h
         — keep in sync). The kernel brick-guard is the AUTHORITATIVE boundary; this mirror lets the
         broker REFUSE TO REGISTER any zone whose baked address falls outside the window the kernel
         would accept, so a malformed or hostile profile is caught loudly at load (Validate/selftest)
         instead of silently emitting writes the kernel then Forbids one-by-one. Defense in depth:
         a profile can never even *attempt* an out-of-window target. --*/
    private const int SmbusRgbMin = 0x70, SmbusRgbMax = 0x77;     // BROKER_SMBUS_RGB_ADDR_*
    private const int SmbusDramMin = 0x39, SmbusDramMax = 0x3A;   // BROKER_SMBUS_DRAM_ADDR_*
    private const int CorsairDramMin = 0x58, CorsairDramMax = 0x5F; // BROKER_RGB_CORSAIR_DRAM_ADDR_*
    private const int Nct6687RgbMin = 0x0F00, Nct6687RgbMax = 0x0FFF; // BROKER_NCT6687_RGB_ADDR_*

    /// <summary>
    /// Broker MIRROR of the kernel g_RgbWriteProfiles windows (Smbus.c) keyed by device class. Returns
    /// true if <paramref name="address"/> is inside the class's allowed window(s). The kernel guard is
    /// authoritative; this lets the broker refuse an out-of-window zone at load instead of per-write.
    /// </summary>
    private static bool InWin(int a, int lo, int hi) => a >= lo && a <= hi;

    private static bool SmbusClassAllows(RgbWriteClass cls, int a) => cls switch
    {
        RgbWriteClass.EneDram        => InWin(a, SmbusRgbMin, SmbusRgbMax) || InWin(a, SmbusDramMin, SmbusDramMax),
        RgbWriteClass.CorsairDram    => InWin(a, CorsairDramMin, CorsairDramMax),
        RgbWriteClass.CrucialDram    => InWin(a, 0x20, 0x27) || InWin(a, 0x39, 0x3C),
        RgbWriteClass.HyperXDram     => a == 0x27,
        RgbWriteClass.FuryDram       => InWin(a, 0x58, 0x67),
        RgbWriteClass.ViperDram      => a == 0x77,
        RgbWriteClass.XtreemDram     => InWin(a, 0x70, 0x78) || InWin(a, 0x39, 0x3D),
        RgbWriteClass.CorsairVenDram => InWin(a, 0x58, 0x5F),
        RgbWriteClass.AsrockMb       => a == 0x6A,
        RgbWriteClass.EvgaMb         => a == 0x28,
        _ => false,   // unknown class — refuse, mirroring the kernel
    };

    /// <summary>
    /// Returns a fault message if <paramref name="z"/>'s baked hardware address is outside the kernel
    /// write window for its transport, else null. The EC path writes 3 color bytes from EcAddress, so
    /// the whole span must fit. UsbHid has no kernel address (bounded by the HID report builder).
    /// </summary>
    public static string? ZoneAddressFault(RgbZone z) => z.Transport switch
    {
        RgbTransport.SmbusEne =>
            (z.Address >= SmbusRgbMin && z.Address <= SmbusRgbMax) ||
            (z.Address >= SmbusDramMin && z.Address <= SmbusDramMax)
                ? null : $"SMBus address 0x{z.Address:X2} is outside the kernel RGB windows",
        RgbTransport.Smbus =>
            SmbusClassAllows(z.WriteClass, z.Address)
                ? null : $"SMBus address 0x{z.Address:X2} is outside the {z.WriteClass} kernel window",
        RgbTransport.SuperioEc =>
            z.EcAddress >= Nct6687RgbMin && z.EcAddress + 2 <= Nct6687RgbMax
                ? null : $"EC address 0x{z.EcAddress:X4} is outside the NCT6687 RGB window",
        RgbTransport.UsbHid =>
            z.Kind == RgbZoneKind.MbArgb && z.LedCount > MysticLightHidController.PerLedMaxLeds
                ? $"per-LED LedCount {z.LedCount} exceeds the {MysticLightHidController.PerLedMaxLeds}-LED direct frame"
                : null,   // UsbHid has no kernel address (bounded by the HID report builder)
        _ => $"unknown transport {z.Transport}",
    };

    /*-- The DRAM zones are identical across the boards we target so far (ENE/Aura at the
         baked 0x39/0x3A this AM4 board reports). Factored out so each profile reuses them. --*/
    private static IReadOnlyList<RgbZone> DramEneZones() => new[]
    {
        new RgbZone("ram0", "GSkill RGB (DIMM 0)", RgbZoneKind.Dram, RgbTransport.SmbusEne, LedCount: 5, Bus: 0, Address: 0x39),
        new RgbZone("ram1", "GSkill RGB (DIMM 1)", RgbZoneKind.Dram, RgbTransport.SmbusEne, LedCount: 5, Bus: 0, Address: 0x3A),
    };

    /// <summary>Board profiles, most specific first; the last entry is the generic wildcard fallback.</summary>
    public static readonly IReadOnlyList<RgbBoardProfile> Profiles = new[]
    {
        /*-- MSI MPG B550I GAMING EDGE MAX WIFI (the dev box). Full zone vocabulary. --*/
        new RgbBoardProfile(
            "Micro-Star International Co., Ltd.",
            "MPG B550I GAMING EDGE MAX WIFI (MS-7C92)",
            new[]
            {
                DramEneZones()[0],
                DramEneZones()[1],
                /* 12V JRGB header via the NCT6687 EC. EcAddress is the PLACEHOLDER RGB window
                   base (BROKER_NCT6687_RGB_ADDR_MIN); it never reaches hardware until the kernel
                   advertises CAP_SUPERIO_RGB (SuperioRgbImplemented), so this zone stays inert
                   until the EC RGB registers are validated. Single solid-color zone (LedCount 1). */
                new RgbZone("mb.jrgb0", "Motherboard 12V Header (JRGB)", RgbZoneKind.Mb12V,
                            RgbTransport.SuperioEc, LedCount: 1, EcAddress: 0x0F00),
                /* 5V addressable JRAINBOW header via USB-HID Mystic Light. Opt-in (AllowHidRgb).
                   PINNED to PID 0x7C92 — the Mystic Light controller on this board (confirmed by
                   --hid-scan: VID 0x1462; the PID matches the board's MS-7C92 model number; the other
                   MSI interface 0x3FA4 has no feature reports and is refused by the pin). The device's
                   max feature length (725) = the 0x53 per-LED DIRECT frame, so this zone is driven
                   PER-LED (literal RGB, linear brightness — no firmware sync-engine fold).

                   HidZoneIndex (31 = j_rainbow_1 byte offset in FeaturePacket_185) is the zone slot
                   used to put THIS zone into direct mode (185-byte enable packet). HidPerLedHdr1/Hdr2
                   are the per-zone selector in the 0x53 per-LED frame: JRAINBOW1 = 4/0 (JRAINBOW2 would
                   be 4/1, JCORSAIR 5/0). The zone's LEDs start at index 0 of its own frame; LedCount 60
                   = the strip length. Confirm the selector on the strip with `--mystic-perled --hdr1=4`;
                   see docs/RGB-BOARD-BRINGUP.md. Brightness/flicker fix HW-validated 2026-06-13/17;
                   per-LED bring-up in progress. */
                new RgbZone("mb.argb0", "MSI JRAINBOW", RgbZoneKind.MbArgb,
                            RgbTransport.UsbHid, LedCount: 60, HidZoneIndex: 31,
                            HidPerLedHdr1: 4, HidPerLedHdr2: 0, HidProductId: 0x7C92),
            }),

        /*-- Generic fallback: DRAM only. Preserves the pre-board-profile behavior on any board
             whose headers we have not mapped yet (no motherboard-header zones asserted). --*/
        new RgbBoardProfile("", "", DramEneZones()),
    };

    /// <summary>
    /// Resolve the active board profile by DMI identity (case-insensitive; a null/empty field in a
    /// profile is a wildcard, matching the sensor calibration matcher). Falls back to the generic
    /// profile. Never returns null.
    /// </summary>
    public static RgbBoardProfile Resolve(BoardIdentity board)
    {
        foreach (RgbBoardProfile p in Profiles)
        {
            if (p.IsGeneric) continue;
            bool mfrOk = string.IsNullOrEmpty(p.Manufacturer) || string.Equals(p.Manufacturer, board.Manufacturer, StringComparison.OrdinalIgnoreCase);
            bool prodOk = string.IsNullOrEmpty(p.Product) || string.Equals(p.Product, board.Product, StringComparison.OrdinalIgnoreCase);
            if (mfrOk && prodOk) return p;
        }
        return Profiles.First(p => p.IsGeneric);
    }

    /// <summary>
    /// Catalog integrity check (run by --selftest): every profile has zones, zone ids are unique
    /// WITHIN a profile, DRAM is always present, and each zone's transport/kind is a defined enum
    /// value. Returns null when clean, otherwise the first violation found.
    /// </summary>
    public static string? Validate()
    {
        bool sawGeneric = false;
        foreach (RgbBoardProfile p in Profiles)
        {
            string who = p.IsGeneric ? "(generic)" : $"{p.Manufacturer} / {p.Product}";
            if (p.IsGeneric) sawGeneric = true;
            if (p.Zones.Count == 0) return $"profile '{who}' has no zones";

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasDram = false;
            foreach (RgbZone z in p.Zones)
            {
                if (string.IsNullOrEmpty(z.Id)) return $"profile '{who}' has a zone with no id";
                if (!ids.Add(z.Id)) return $"profile '{who}' has duplicate zone id '{z.Id}'";
                if (!Enum.IsDefined(typeof(RgbTransport), z.Transport)) return $"zone '{z.Id}' has an undefined transport";
                if (!Enum.IsDefined(typeof(RgbZoneKind), z.Kind)) return $"zone '{z.Id}' has an undefined kind";
                if (z.LedCount < 1) return $"zone '{z.Id}' has a non-positive LedCount";
                string? addrFault = ZoneAddressFault(z);   // every baked address must be inside a kernel window
                if (addrFault != null) return $"zone '{z.Id}': {addrFault}";
                if (z.Kind == RgbZoneKind.Dram) hasDram = true;
            }
            if (!hasDram) return $"profile '{who}' declares no DRAM zone";
        }
        if (!sawGeneric) return "no generic fallback profile is defined";
        return null;
    }

    /// <summary>The standard zone kinds a profile does NOT cover (the parity gap, for startup logging).</summary>
    public static IReadOnlyList<RgbZoneKind> MissingKinds(RgbBoardProfile profile)
    {
        var present = profile.Zones.Select(z => z.Kind).ToHashSet();
        return StandardKinds.Where(k => !present.Contains(k)).ToList();
    }
}
