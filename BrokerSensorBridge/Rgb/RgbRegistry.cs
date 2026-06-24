namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| RgbRegistry                                                                |
|                                                                            |
|   The control service's live set of drivable RGB zones, built at startup    |
|   from the DMI-matched board profile (RgbCatalog) crossed with the          |
|   transports actually present:                                              |
|     * SmbusEne  DRAM  — kernel SMBus write path (CAP_WRITE).                 |
|     * SuperioEc 12V   — kernel NCT6687 EC RGB write path (CAP_SUPERIO_RGB;   |
|                         inert until the EC RGB window is HW-validated).      |
|     * UsbHid    ARGB  — MSI Mystic Light USB-HID, opt-in (AllowHidRgb).      |
|                                                                            |
|   Each zone contributes a device only when its transport is available, so    |
|   nothing appears on the wrong board / a host without that path. Per-zone    |
|   labels come from calibration (addresses-free — relabel/hide only).         |
|                                                                            |
|   The MSI Mystic Light USB-HID transport is the corrected re-introduction    |
|   of a user-mode HID path (the retired Gigabyte IT8297 stays retired —       |
|   design record: docs/GIGABYTE-SUPPORT.md); it is gated and reduced-         |
|   assurance (no kernel brick-guard), see SECURITY.md.                        |
\*---------------------------------------------------------------------------*/
internal sealed class RgbRegistry : IDisposable
{
    private const ushort MysticLightVendorId = 0x1462;

    private readonly List<IRgbController> _devices;
    private readonly List<HidDevice> _hid;

    private RgbRegistry(List<IRgbController> devices, List<HidDevice> hid)
    {
        _devices = devices; _hid = hid;
    }

    public IReadOnlyList<IRgbController> Devices => _devices;
    public bool Any => _devices.Count > 0;

    public IRgbController? Find(string id) =>
        _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public static RgbRegistry Build(ISmbusBackend smbus, BoardIdentity board, CalibrationStore calib,
                                    bool allowHidRgb, bool allowUnpinnedHid, Action<string> log)
    {
        RgbBoardProfile profile = RgbCatalog.Resolve(board);
        log($"[rgb] board profile: {(profile.IsGeneric ? "(generic fallback)" : $"{profile.Manufacturer} / {profile.Product}")}, {profile.Zones.Count} zone(s)");

        string? invalid = RgbCatalog.Validate();
        if (invalid != null) log($"[rgb] WARNING: RGB catalog invalid: {invalid}");

        IReadOnlyList<RgbZoneKind> missing = RgbCatalog.MissingKinds(profile);
        if (!profile.IsGeneric && missing.Count > 0)
            log($"[rgb] parity: profile does not cover {string.Join(", ", missing)} (no hardware mapped on this board)");

        var list = new List<IRgbController>();
        var hidDevices = new List<HidDevice>();
        var usedHid = new HashSet<HidDevice>();   // HID interfaces actually bound to a zone
        IReadOnlyList<HidDevice>? hids = null;   // opened lazily, only if a UsbHid zone needs it

        foreach (RgbZone z in profile.Zones)
        {
            ChannelOverride o = calib.Resolve(z.Id);
            if (o.Hidden) { log($"[rgb] zone '{z.Id}' hidden by calibration; skipped"); continue; }
            RgbZone zone = o.Label != null ? z with { Label = o.Label } : z;

            /* Defense in depth: refuse to register a zone whose baked address is outside the kernel
               write window for its transport (the kernel would Forbid it anyway). A malformed or
               hostile profile can never even attempt an out-of-window target. */
            string? fault = RgbCatalog.ZoneAddressFault(zone);
            if (fault != null) { log($"[rgb] REFUSED zone '{zone.Id}': {fault}"); continue; }

            switch (zone.Transport)
            {
                case RgbTransport.SmbusEne when smbus.WriteAvailable:
                    list.Add(new EneRgbController(smbus, zone));
                    break;

                case RgbTransport.SuperioEc when smbus.SuperioRgbAvailable:
                    list.Add(new MysticLightEcController(smbus, zone));
                    break;

                case RgbTransport.UsbHid when allowHidRgb:
                    hids ??= OpenMysticLightHid(hidDevices, log);
                    int sel = SelectHidIndex(hids.Select(h => h.ProductId).ToList(), zone.HidProductId);
                    if (sel >= 0)
                    {
                        /* An UNPINNED zone (HidProductId == 0) binds the FIRST VID-matched device, which
                           on a board with several MSI HID interfaces could be the WRONG one — a write to
                           a non-positively-matched device. That is a board-BRING-UP convenience only: in
                           a normal build refuse to drive it, so a tester/user never writes to a device we
                           did not positively identify. --rgb-allow-unpinned-hid re-enables it for bring-up
                           (use --hid-scan to find the PID, then pin HidProductId in the profile). */
                        if (zone.HidProductId == 0 && !allowUnpinnedHid)
                        {
                            log($"[rgb] zone '{zone.Id}': UNPINNED Mystic Light HID not driven (bring-up only; "
                              + "set --rgb-allow-unpinned-hid to bind, then pin HidProductId). skipped");
                            break;
                        }
                        log($"[rgb] zone '{zone.Id}' -> HID PID 0x{hids[sel].ProductId:X4}"
                          + (zone.HidProductId != 0 ? " (pinned)" : " (UNPINNED — bring-up only; pin HidProductId)"));
                        list.Add(new MysticLightHidController(hids[sel], zone));
                        usedHid.Add(hids[sel]);
                    }
                    else if (zone.HidProductId != 0)
                        log($"[rgb] zone '{zone.Id}': pinned Mystic Light PID 0x{zone.HidProductId:X4} not found at VID 0x{MysticLightVendorId:X4}; skipped");
                    else
                        log($"[rgb] zone '{zone.Id}': no Mystic Light HID device (VID 0x{MysticLightVendorId:X4}) found; skipped");
                    break;

                default:
                    log($"[rgb] zone '{zone.Id}' ({zone.Kind}/{zone.Transport}) not available on this host; skipped");
                    break;
            }
        }

        /* Board-independent SMBus DRAM RGB (Corsair/Crucial/T-Force/...): not part of the DMI board
           profile — probed directly on each family's RGB address window (all above SPD) and matched by a
           positive signature AT THE RGB ADDRESS (never via SPD, which the kernel refuses to read). FULL
           assurance (kernel brick-guarded per device class). HW-UNVALIDATED on this dev box; only a
           positively-identified stick is ever driven. Read-only signatures are tried before any
           identify-writes, and the first family to claim an address wins. */
        if (smbus.WriteAvailable)
        {
            DetectSmbusDram(smbus, list, log);
            DetectMotherboardSmbus(smbus, board, list, log);
        }

        /* Board-independent USB-HID peripherals (Razer Chroma keyboards/mice): not part of the
           DMI board profile — enumerated directly by vendor id (0x1532) and matched against the
           known-model table. Same opt-in gate (AllowHidRgb) and reduced assurance (no kernel
           brick-guard) as the Mystic Light path; the RGB-control interface is the one presenting
           a 91-byte feature report. */
        if (allowHidRgb)
        {
            IReadOnlyList<HidDevice> razer = OpenRazerHid(hidDevices, log);
            foreach (RazerHidController.Model m in RazerHidController.KnownModels)
            {
                /* The 90-byte command collection is identified by the (interface, usage page, usage)
                   tuple OpenRGB's Razer detector uses — a device exposes several collections on one
                   interface, so the usage disambiguates the command one (usage 0x01:0x02, 91-byte
                   feature report) from the consumer/system collections that share the interface. */
                HidDevice? iface = razer.FirstOrDefault(h =>
                    h.ProductId == m.Pid && h.InterfaceNumber == m.Interface
                    && h.UsagePage == m.UsagePage && h.Usage == m.Usage && !usedHid.Contains(h));
                if (iface != null)
                {
                    log($"[rgb] Razer {m.Label} (PID 0x{m.Pid:X4} iface {m.Interface} usage {m.UsagePage:X2}:{m.Usage:X2}, featureLen {iface.FeatureReportByteLength}) -> bound (USB-HID, reduced assurance)");
                    list.Add(new RazerHidController(iface, m));
                    usedHid.Add(iface);
                }
                else if (razer.Any(h => h.ProductId == m.Pid))
                    log($"[rgb] Razer {m.Label} (PID 0x{m.Pid:X4}): device present but command collection "
                      + $"(iface {m.Interface} usage {m.UsagePage:X2}:{m.Usage:X2}) not found/openable "
                      + "(Razer Synapse may hold it exclusively, or the interface map differs) — skipped");
            }
        }

        /* Board-independent USB-HID coolers (AMD Wraith Prism): matched by USB id, on the vendor
           collection (interface 1, usage page 0xFF00). Same opt-in gate + reduced assurance as the
           other HID paths. Output-report transport (HidD_SetOutputReport on the zero-access handle). */
        if (allowHidRgb)
        {
            IReadOnlyList<HidDevice> wraith = HidDevice.OpenByVendor(WraithPrismController.UsbVendorId, log);
            hidDevices.AddRange(wraith);
            HidDevice? iface = wraith.FirstOrDefault(h =>
                h.ProductId == WraithPrismController.UsbProductId
                && h.InterfaceNumber == WraithPrismController.CommandInterface
                && h.UsagePage == WraithPrismController.VendorUsagePage && !usedHid.Contains(h));
            if (iface != null)
            {
                log($"[rgb] AMD Wraith Prism (PID 0x{iface.ProductId:X4} iface {iface.InterfaceNumber} usage {iface.UsagePage:X2}:{iface.Usage:X2}) -> bound (USB-HID, reduced assurance)");
                list.Add(new WraithPrismRgbController(iface));
                usedHid.Add(iface);
            }
            else if (wraith.Any(h => h.ProductId == WraithPrismController.UsbProductId))
                log("[rgb] AMD Wraith Prism present but the vendor command collection (iface 1, usage FF00) was not found/openable — skipped");

            DetectLogitech(hidDevices, usedHid, list, log);
            DetectSteelSeries(hidDevices, usedHid, list, log);
            DetectCorsairV2(hidDevices, usedHid, list, log);
            DetectNzxt(hidDevices, usedHid, list, log);
            DetectRoccat(hidDevices, usedHid, list, log);
            DetectRedragon(hidDevices, usedHid, list, log);
            DetectCoolerMaster(hidDevices, usedHid, list, log);
            DetectHyperX(hidDevices, usedHid, list, log);
            DetectAsus(hidDevices, usedHid, list, log);
            DetectLianLi(hidDevices, usedHid, list, log);
        }

        /* Close any opened MSI HID interfaces that no zone selected (e.g. the non-RGB MSI
           interface enumerated alongside the controller) instead of holding their handles
           open for the whole service lifetime. Only zone-bound interfaces survive in _hid. */
        for (int i = hidDevices.Count - 1; i >= 0; i--)
        {
            if (!usedHid.Contains(hidDevices[i]))
            {
                log($"[rgb] closing unused HID interface PID 0x{hidDevices[i].ProductId:X4} (no zone bound)");
                hidDevices[i].Dispose();
                hidDevices.RemoveAt(i);
            }
        }

        log($"[rgb] registry: {list.Count} device(s) [{string.Join(", ", list.Select(d => d.Id))}]");
        return new RgbRegistry(list, hidDevices);
    }

    /*-- Per-family RGB address windows (all above SPD 0x50-0x57). DRAM RGB is matched by a positive
         signature at THESE addresses, never via SPD (which the kernel refuses), so the project's
         anti-brick SPD guard is preserved. NOTE: HyperX (ACK-only at 0x27) and Patriot Viper (SPD
         signature) are deliberately NOT auto-detected — their identity lives in SPD, which is
         unreadable here; their controllers exist but need an SPD-signature path that conflicts with
         the SPD-refusal guardrail (documented gap). --*/
    private static readonly int[] CorsairDramAddrs = { 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F };
    private static readonly int[] CrucialAddrs     = { 0x20, 0x21, 0x22, 0x23, 0x27, 0x39, 0x3A, 0x3B, 0x3C };
    private static readonly int[] XtreemAddrs      = { 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x78, 0x39, 0x3A, 0x3B, 0x3C, 0x3D };

    /// <summary>
    /// Probes each DRAM RGB family on its address window and registers a controller per positively-
    /// identified stick. First family to claim an address wins (a claimed-set prevents double-binding
    /// the overlapping 0x39-0x3C band). Read-only signatures are checked before any identify-writes.
    /// Bus 0 (the primary FCH segment the DIMMs sit on).
    /// </summary>
    private static void DetectSmbusDram(ISmbusBackend smbus, List<IRgbController> sink, Action<string> log)
    {
        var claimed = new HashSet<int>();
        int n = 0;
        void Claim(int addr, IRgbController dev, string detail)
        {
            claimed.Add(addr);
            log($"[rgb] DRAM RGB at 0x{addr:X2}: {detail} -> '{dev.Id}' [SMBus]");
            sink.Add(dev);
            n++;
        }

        // Corsair family (0x58-0x5F): read-only 0xBA signature (Vengeance) before the device-info probe (newer).
        foreach (int addr in CorsairDramAddrs)
        {
            if (claimed.Contains(addr)) continue;
            if (CorsairVengeanceController.TryIdentify(smbus, 0, addr))
            {
                Claim(addr, new CorsairVengeanceRgbController(smbus, $"corsair.ven{n}", "Corsair Vengeance RGB", 0, addr), "Corsair Vengeance (0xBA sig)");
            }
            else if (CorsairDramController.TryIdentify(smbus, 0, addr, out ushort pid, out byte protocol)
                     && CorsairDramController.SupportsDirect(protocol))
            {
                CorsairDramController.Model m = CorsairDramController.ResolveModel(pid);
                Claim(addr, new CorsairDramRgbController(smbus, $"corsair.dram{n}", m.Name, 0, addr, m.LedCount, m.Reverse),
                      $"{m.Name} (PID 0x{pid:X4} proto {protocol}, {m.LedCount} LEDs)");
            }
        }

        // T-Force Xtreem (ENE ramp 0x90..0xA0) — checked before Crucial on the shared 0x39-0x3C band.
        foreach (int addr in XtreemAddrs)
        {
            if (claimed.Contains(addr)) continue;
            if (TForceXtreemController.TryIdentify(smbus, 0, addr))
                Claim(addr, new TForceXtreemRgbController(smbus, $"tforce.dram{n}", "T-Force Xtreem RGB", 0, addr), "T-Force Xtreem (ENE ramp)");
        }

        // Crucial Ballistix (ramp 0xA0..0xAF + "Micron").
        foreach (int addr in CrucialAddrs)
        {
            if (claimed.Contains(addr)) continue;
            if (CrucialDramController.TryIdentify(smbus, 0, addr))
                Claim(addr, new CrucialDramRgbController(smbus, $"crucial.dram{n}", "Crucial Ballistix RGB", 0, addr), "Crucial Ballistix (Micron sig)");
        }

        if (n > 0) log($"[rgb] SMBus DRAM RGB: {n} stick(s) registered");
    }

    /// <summary>
    /// Probes motherboard-RGB SMBus controllers (ASRock 0x6A / EVGA 0x28), GATED on the board's DMI
    /// manufacturer so a non-ASRock/non-EVGA board is never poked at those addresses (mirrors OpenRGB's
    /// subsystem-vendor gate). Each device gets one solid whole-board zone. Bus 0.
    /// </summary>
    private static void DetectMotherboardSmbus(ISmbusBackend smbus, BoardIdentity board, List<IRgbController> sink, Action<string> log)
    {
        string mfr = board.Manufacturer ?? "";
        if (mfr.Contains("ASRock", StringComparison.OrdinalIgnoreCase))
        {
            if (AsrockMbController.TryIdentify(smbus, 0, 0x6A, out int variant, out int[] zones))
            {
                log($"[rgb] ASRock motherboard RGB at 0x6A: firmware variant 0x{variant:X2}, {zones.Length} zone(s) -> 'mb.asrock' [SMBus, AsrockMb class]");
                sink.Add(new AsrockMbRgbController(smbus, "mb.asrock", "ASRock Motherboard RGB", 0, 0x6A, variant, zones));
            }
        }
        else if (mfr.Contains("EVGA", StringComparison.OrdinalIgnoreCase))
        {
            if (EvgaMbController.TryIdentify(smbus, 0, 0x28))
            {
                log("[rgb] EVGA motherboard RGB at 0x28 -> 'mb.evga' [SMBus, EvgaMb class]");
                sink.Add(new EvgaMbRgbController(smbus, "mb.evga", "EVGA Motherboard RGB", 0, 0x28));
            }
        }
    }

    /// <summary>
    /// Detects Logitech G-series HID RGB (VID 0x046D): the G203 Lightsync mouse (iface 1, usage
    /// FF00:0002) and the G810/G910/G Pro keyboards (iface 1, usage FF43:0602). Opt-in, reduced
    /// assurance. Keyboards are driven as whole-keyboard solid color (firmware STATIC mode).
    /// </summary>
    private static void DetectLogitech(List<HidDevice> sink, HashSet<HidDevice> usedHid, List<IRgbController> list, Action<string> log)
    {
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(Logitech.VendorId, log);
        sink.AddRange(devs);

        HidDevice? g203 = devs.FirstOrDefault(h =>
            LogitechG203LController.ProductIds.Contains(h.ProductId)
            && h.InterfaceNumber == LogitechG203LController.CommandInterface
            && h.UsagePage == LogitechG203LController.UsagePage && h.Usage == LogitechG203LController.Usage
            && !usedHid.Contains(h));
        if (g203 != null)
        {
            log($"[rgb] Logitech G203 Lightsync (PID 0x{g203.ProductId:X4}) -> bound (USB-HID, reduced assurance)");
            list.Add(new LogitechG203LRgbController(g203));
            usedHid.Add(g203);
        }

        foreach (LogitechGKeyboardController.Model m in LogitechGKeyboardController.KnownModels)
        {
            HidDevice? kb = devs.FirstOrDefault(h =>
                h.ProductId == m.Pid && h.InterfaceNumber == LogitechGKeyboardController.CommandInterface
                && h.UsagePage == LogitechGKeyboardController.UsagePage && h.Usage == LogitechGKeyboardController.Usage
                && !usedHid.Contains(h));
            if (kb != null)
            {
                log($"[rgb] {m.Label} (PID 0x{m.Pid:X4}) -> bound (USB-HID solid color, reduced assurance)");
                list.Add(new LogitechGKeyboardRgbController(kb, m));
                usedHid.Add(kb);
            }
        }
    }

    /// <summary>
    /// Detects SteelSeries HID mice (VID 0x1038, iface 3, usage FFC0:0001): Rival 3 and Aerox 3/5.
    /// Opt-in, reduced assurance, zone-based solid color.
    /// </summary>
    private static void DetectSteelSeries(List<HidDevice> sink, HashSet<HidDevice> usedHid, List<IRgbController> list, Action<string> log)
    {
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(SteelSeries.VendorId, log);
        sink.AddRange(devs);

        bool Match(HidDevice h, ushort pid) =>
            h.ProductId == pid && h.InterfaceNumber == SteelSeries.CommandInterface
            && h.UsagePage == SteelSeries.UsagePage && h.Usage == SteelSeries.Usage && !usedHid.Contains(h);

        HidDevice? rival3 = devs.FirstOrDefault(h => SteelSeriesRival3Controller.ProductIds.Any(p => Match(h, p)));
        if (rival3 != null)
        {
            log($"[rgb] SteelSeries Rival 3 (PID 0x{rival3.ProductId:X4}) -> bound (USB-HID, reduced assurance)");
            list.Add(new SteelSeriesRival3RgbController(rival3));
            usedHid.Add(rival3);
        }

        foreach (SteelSeriesAeroxController.Model m in SteelSeriesAeroxController.KnownModels)
        {
            HidDevice? aerox = devs.FirstOrDefault(h => Match(h, m.Pid));
            if (aerox != null)
            {
                log($"[rgb] {m.Label} (PID 0x{m.Pid:X4}) -> bound (USB-HID, reduced assurance)");
                list.Add(new SteelSeriesAeroxRgbController(aerox, m));
                usedHid.Add(aerox);
            }
        }

        // Apex 3 keyboards (8-zone / T-zone) — per-model interface + usage.
        foreach (SteelSeriesApex3Controller.Model m in SteelSeriesApex3Controller.KnownModels)
        {
            HidDevice? kb = devs.FirstOrDefault(h =>
                h.ProductId == m.Pid && h.InterfaceNumber == m.Interface
                && h.UsagePage == m.UsagePage && h.Usage == m.Usage && !usedHid.Contains(h));
            if (kb != null)
            {
                log($"[rgb] {m.Label} (PID 0x{m.Pid:X4}, {m.Zones} zones) -> bound (USB-HID, reduced assurance)");
                list.Add(new SteelSeriesApex3RgbController(kb, m));
                usedHid.Add(kb);
            }
        }

        // OldApex 5-zone keyboards + Sensei/Rival 310 mice — interface 0, no usage filter.
        HidDevice? oldApex = devs.FirstOrDefault(h => SteelSeriesOldApexController.ProductIds.Contains(h.ProductId)
            && h.InterfaceNumber == SteelSeriesOldApexController.Interface && !usedHid.Contains(h));
        if (oldApex != null)
        {
            log($"[rgb] SteelSeries Apex OG/350 (PID 0x{oldApex.ProductId:X4}) -> bound (USB-HID, reduced assurance)");
            list.Add(new SteelSeriesOldApexRgbController(oldApex)); usedHid.Add(oldApex);
        }
        HidDevice? sensei = devs.FirstOrDefault(h => SteelSeriesSenseiController.ProductIds.Contains(h.ProductId)
            && h.InterfaceNumber == SteelSeriesSenseiController.Interface && !usedHid.Contains(h));
        if (sensei != null)
        {
            log($"[rgb] SteelSeries Sensei/Rival 310 (PID 0x{sensei.ProductId:X4}) -> bound (USB-HID, reduced assurance)");
            list.Add(new SteelSeriesSenseiRgbController(sensei)); usedHid.Add(sensei);
        }

        // Rival 100 / 300 legacy mice — interface 0, no usage filter.
        foreach (SteelSeriesRivalLegacyController.Model m in SteelSeriesRivalLegacyController.KnownModels)
        {
            HidDevice? r = devs.FirstOrDefault(h => m.Pids.Contains(h.ProductId)
                && h.InterfaceNumber == SteelSeriesRivalLegacyController.Interface && !usedHid.Contains(h));
            if (r != null)
            {
                log($"[rgb] {m.Label} (PID 0x{r.ProductId:X4}) -> bound (USB-HID, reduced assurance)");
                list.Add(new SteelSeriesRivalLegacyRgbController(r, m)); usedHid.Add(r);
            }
        }
    }

    /// <summary>Detects the NZXT Lift mouse (VID 0x1E71, PID 0x2100, iface 0, usage FFCA:0001).</summary>
    private static void DetectNzxt(List<HidDevice> sink, HashSet<HidDevice> usedHid, List<IRgbController> list, Action<string> log)
    {
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(NzxtLiftController.VendorId, log);
        sink.AddRange(devs);
        HidDevice? lift = devs.FirstOrDefault(h => h.ProductId == NzxtLiftController.ProductId
            && h.InterfaceNumber == NzxtLiftController.Interface
            && h.UsagePage == NzxtLiftController.UsagePage && h.Usage == NzxtLiftController.Usage && !usedHid.Contains(h));
        if (lift != null)
        {
            log($"[rgb] NZXT Lift (PID 0x{lift.ProductId:X4}) -> bound (USB-HID, reduced assurance)");
            list.Add(new NzxtLiftRgbController(lift)); usedHid.Add(lift);
        }
    }

    /// <summary>Detects Roccat mice (VID 0x1E7D): Kone Aimo (iface 0, usage 0B:00) and Kone Pro (iface 3, usage FF01:01).</summary>
    private static void DetectRoccat(List<HidDevice> sink, HashSet<HidDevice> usedHid, List<IRgbController> list, Action<string> log)
    {
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(Roccat.VendorId, log);
        sink.AddRange(devs);

        HidDevice? aimo = devs.FirstOrDefault(h => RoccatKoneAimoController.ProductIds.Contains(h.ProductId)
            && h.InterfaceNumber == RoccatKoneAimoController.Interface
            && h.UsagePage == RoccatKoneAimoController.UsagePage && h.Usage == RoccatKoneAimoController.Usage && !usedHid.Contains(h));
        if (aimo != null)
        {
            log($"[rgb] Roccat Kone Aimo (PID 0x{aimo.ProductId:X4}) -> bound (USB-HID, reduced assurance)");
            list.Add(new RoccatKoneAimoRgbController(aimo)); usedHid.Add(aimo);
        }
        HidDevice? konepro = devs.FirstOrDefault(h => h.ProductId == RoccatKoneProController.ProductId
            && h.InterfaceNumber == RoccatKoneProController.Interface
            && h.UsagePage == RoccatKoneProController.UsagePage && h.Usage == RoccatKoneProController.Usage && !usedHid.Contains(h));
        if (konepro != null)
        {
            log($"[rgb] Roccat Kone Pro (PID 0x{konepro.ProductId:X4}) -> bound (USB-HID, reduced assurance)");
            list.Add(new RoccatKoneProRgbController(konepro)); usedHid.Add(konepro);
        }
    }

    /// <summary>
    /// Detects Corsair iCUE V2 HID peripherals (VID 0x1B1C, iface 1, usage 0xFF42) with a FIXED LED
    /// count (mice, mousepads, K55). Opt-in, reduced assurance. The matrix keyboards are excluded
    /// (layout-dependent LED count — see docs/RGB-DEVICE-COVERAGE.md).
    /// </summary>
    private static void DetectCorsairV2(List<HidDevice> sink, HashSet<HidDevice> usedHid, List<IRgbController> list, Action<string> log)
    {
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(CorsairV2Controller.VendorId, log);
        sink.AddRange(devs);

        foreach (CorsairV2Controller.Model m in CorsairV2Controller.KnownModels)
        {
            HidDevice? dev = devs.FirstOrDefault(h =>
                h.ProductId == m.Pid && h.InterfaceNumber == CorsairV2Controller.CommandInterface
                && h.Usage == CorsairV2Controller.Usage && !usedHid.Contains(h));
            if (dev != null)
            {
                log($"[rgb] {m.Label} (PID 0x{m.Pid:X4}, {m.Leds} LEDs) -> bound (USB-HID iCUE V2, reduced assurance)");
                list.Add(new CorsairV2RgbController(dev, m));
                usedHid.Add(dev);
            }
        }
    }

    /// <summary>Detects Redragon mice (VID 0x04D9, iface 2, usage page 0xFFA0 — usage unfiltered).</summary>
    private static void DetectRedragon(List<HidDevice> sink, HashSet<HidDevice> usedHid, List<IRgbController> list, Action<string> log)
    {
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(RedragonMouseController.VendorId, log);
        sink.AddRange(devs);
        HidDevice? m = devs.FirstOrDefault(h => RedragonMouseController.ProductIds.Contains(h.ProductId)
            && h.InterfaceNumber == RedragonMouseController.Interface
            && h.UsagePage == RedragonMouseController.UsagePage && !usedHid.Contains(h));
        if (m != null)
        {
            log($"[rgb] Redragon mouse (PID 0x{m.ProductId:X4}) -> bound (USB-HID, reduced assurance)");
            list.Add(new RedragonMouseRgbController(m, m.ProductId)); usedHid.Add(m);
        }
    }

    /// <summary>Detects the Cooler Master MP750 mousepad (VID 0x2516, usage FF00:0001).</summary>
    private static void DetectCoolerMaster(List<HidDevice> sink, HashSet<HidDevice> usedHid, List<IRgbController> list, Action<string> log)
    {
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(CoolerMasterMp750Controller.VendorId, log);
        sink.AddRange(devs);
        HidDevice? mp = devs.FirstOrDefault(h => CoolerMasterMp750Controller.ProductIds.Contains(h.ProductId)
            && h.UsagePage == CoolerMasterMp750Controller.UsagePage && h.Usage == CoolerMasterMp750Controller.Usage && !usedHid.Contains(h));
        if (mp != null)
        {
            log($"[rgb] Cooler Master MP750 (PID 0x{mp.ProductId:X4}) -> bound (USB-HID, reduced assurance)");
            list.Add(new CoolerMasterMp750RgbController(mp)); usedHid.Add(mp);
        }

        foreach (CoolerMasterMouseController.Model m in CoolerMasterMouseController.KnownModels)
        {
            HidDevice? mouse = devs.FirstOrDefault(h => h.ProductId == m.Pid
                && h.InterfaceNumber == CoolerMasterMouseController.Interface
                && h.UsagePage == CoolerMasterMouseController.UsagePage && h.Usage == CoolerMasterMouseController.Usage && !usedHid.Contains(h));
            if (mouse != null)
            {
                log($"[rgb] {m.Label} (PID 0x{m.Pid:X4}) -> bound (USB-HID, reduced assurance)");
                list.Add(new CoolerMasterMouseRgbController(mouse, m)); usedHid.Add(mouse);
            }
        }
    }

    /// <summary>
    /// Detects HyperX peripherals across both vendor ids (0x0951 Kingston / 0x03F0 HP). Every HyperX
    /// device uses the broker keepalive loop. Opt-in, reduced assurance.
    /// </summary>
    private static void DetectHyperX(List<HidDevice> sink, HashSet<HidDevice> usedHid, List<IRgbController> list, Action<string> log)
    {
        var devs = new List<HidDevice>();
        foreach (ushort vid in new ushort[] { 0x0951, 0x03F0 })
        {
            IReadOnlyList<HidDevice> v = HidDevice.OpenByVendor(vid, log);
            sink.AddRange(v); devs.AddRange(v);
        }

        void Bind(HidDevice? h, IRgbController ctrl)
        {
            if (h == null) return;
            log($"[rgb] {ctrl.Label} (PID 0x{h.ProductId:X4}) -> bound (USB-HID keepalive, reduced assurance)");
            list.Add(ctrl); usedHid.Add(h);
        }
        bool Free(HidDevice h) => !usedHid.Contains(h);

        // Mice
        HidDevice? fps = devs.FirstOrDefault(h => HyperXPulsefireFpsProController.Ids.Contains((h.VendorId, h.ProductId))
            && h.InterfaceNumber == HyperXPulsefireFpsProController.Interface && h.UsagePage == HyperXPulsefireFpsProController.UsagePage && Free(h));
        if (fps != null) Bind(fps, new HyperXPulsefireFpsProController(fps));

        HidDevice? raid = devs.FirstOrDefault(h => h.VendorId == HyperXPulsefireRaidController.Vid && h.ProductId == HyperXPulsefireRaidController.Pid
            && h.InterfaceNumber == HyperXPulsefireRaidController.Interface && h.UsagePage == HyperXPulsefireRaidController.UsagePage && Free(h));
        if (raid != null) Bind(raid, new HyperXPulsefireRaidController(raid));

        HidDevice? haste = devs.FirstOrDefault(h => HyperXPulsefireHasteController.Ids.Contains((h.VendorId, h.ProductId))
            && h.InterfaceNumber == HyperXPulsefireHasteController.Interface && h.UsagePage == HyperXPulsefireHasteController.UsagePage && Free(h));
        if (haste != null) Bind(haste, new HyperXPulsefireHasteController(haste));

        HidDevice? dart = devs.FirstOrDefault(h => HyperXPulsefireDartController.Ids.Any(
            t => t.Vid == h.VendorId && t.Pid == h.ProductId && t.Iface == h.InterfaceNumber && t.UsagePage == h.UsagePage) && Free(h));
        if (dart != null) Bind(dart, new HyperXPulsefireDartController(dart));

        HidDevice? surge = devs.FirstOrDefault(h => HyperXPulsefireSurgeController.Ids.Contains((h.VendorId, h.ProductId))
            && h.InterfaceNumber == HyperXPulsefireSurgeController.Interface && h.UsagePage == HyperXPulsefireSurgeController.UsagePage && Free(h));
        if (surge != null) Bind(surge, new HyperXPulsefireSurgeController(surge));

        // Keyboards
        HidDevice? afps = devs.FirstOrDefault(h => h.VendorId == HyperXAlloyFpsController.Vid && h.ProductId == HyperXAlloyFpsController.Pid
            && h.InterfaceNumber == HyperXAlloyFpsController.Interface && h.UsagePage == HyperXAlloyFpsController.UsagePage && Free(h));
        if (afps != null) Bind(afps, new HyperXAlloyFpsController(afps));

        foreach (HyperXOriginsController.Model m in HyperXOriginsController.KnownModels)
        {
            HidDevice? kb = devs.FirstOrDefault(h => h.VendorId == m.Vid && h.ProductId == m.Pid
                && h.InterfaceNumber == m.Interface && (m.UsagePage == 0 || h.UsagePage == m.UsagePage) && Free(h));
            if (kb != null) Bind(kb, new HyperXOriginsController(kb, m));
        }
        foreach (HyperX44KeyboardController.Model m in HyperX44KeyboardController.KnownModels)
        {
            HidDevice? kb = devs.FirstOrDefault(h => h.VendorId == m.Vid && h.ProductId == m.Pid
                && h.InterfaceNumber == m.Interface && Free(h));
            if (kb != null) Bind(kb, new HyperX44KeyboardController(kb, m));
        }
    }

    /// <summary>Detects the ASUS ROG Ally handheld (VID 0x0B05, iface 2, usage FF31:0076).</summary>
    private static void DetectAsus(List<HidDevice> sink, HashSet<HidDevice> usedHid, List<IRgbController> list, Action<string> log)
    {
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(AsusRogAllyController.VendorId, log);
        sink.AddRange(devs);
        HidDevice? ally = devs.FirstOrDefault(h => AsusRogAllyController.ProductIds.Contains(h.ProductId)
            && h.InterfaceNumber == AsusRogAllyController.Interface
            && h.UsagePage == AsusRogAllyController.UsagePage && h.Usage == AsusRogAllyController.Usage && !usedHid.Contains(h));
        if (ally != null)
        {
            log($"[rgb] ASUS ROG Ally (PID 0x{ally.ProductId:X4}) -> bound (USB-HID, reduced assurance)");
            list.Add(new AsusRogAllyRgbController(ally)); usedHid.Add(ally);
        }
    }

    /// <summary>Detects the Lian Li Uni Hub SL V2 family (VID 0x0CF2, iface 1, usage FF72:00A1).</summary>
    private static void DetectLianLi(List<HidDevice> sink, HashSet<HidDevice> usedHid, List<IRgbController> list, Action<string> log)
    {
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(LianLiUniHubSlV2Controller.VendorId, log);
        sink.AddRange(devs);
        HidDevice? hub = devs.FirstOrDefault(h => LianLiUniHubSlV2Controller.ProductIds.Contains(h.ProductId)
            && h.InterfaceNumber == LianLiUniHubSlV2Controller.Interface
            && h.UsagePage == LianLiUniHubSlV2Controller.UsagePage && h.Usage == LianLiUniHubSlV2Controller.Usage && !usedHid.Contains(h));
        if (hub != null)
        {
            log($"[rgb] Lian Li Uni Hub SL V2 (PID 0x{hub.ProductId:X4}) -> bound (USB-HID, 1 fan/channel baseline, reduced assurance)");
            list.Add(new LianLiUniHubSlV2RgbController(hub)); usedHid.Add(hub);
        }
    }

    private static IReadOnlyList<HidDevice> OpenMysticLightHid(List<HidDevice> sink, Action<string> log)
    {
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(MysticLightVendorId, log);
        sink.AddRange(devs);
        string pids = devs.Count > 0 ? string.Join(", ", devs.Select(d => $"0x{d.ProductId:X4}")) : "none";
        log($"[rgb] Mystic Light HID: {devs.Count} device(s) at VID 0x{MysticLightVendorId:X4} "
          + $"[candidate PIDs: {pids}] (USB-HID transport — reduced assurance, no kernel brick-guard)");
        return devs;
    }

    private static IReadOnlyList<HidDevice> OpenRazerHid(List<HidDevice> sink, Action<string> log)
    {
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(RazerHidController.RazerVendorId, log);
        sink.AddRange(devs);
        log($"[rgb] Razer HID: {devs.Count} interface(s) at VID 0x{RazerHidController.RazerVendorId:X4} "
          + "(USB-HID transport — reduced assurance, no kernel brick-guard)");
        foreach (HidDevice d in devs)
            log($"[rgb]   PID 0x{d.ProductId:X4} iface {d.InterfaceNumber} featureLen {d.FeatureReportByteLength} usage {d.UsagePage:X2}:{d.Usage:X2}");
        return devs;
    }

    /// <summary>
    /// Select which enumerated MSI HID device a zone drives. When <paramref name="pinnedPid"/> is
    /// non-zero, returns the index of the candidate with that exact USB product id (or -1 if none —
    /// the zone is refused rather than driving a wrong device). When 0 (unpinned), returns the first
    /// candidate, or -1 if none. Pure/testable — no device handles.
    /// </summary>
    internal static int SelectHidIndex(IReadOnlyList<ushort> candidatePids, int pinnedPid)
    {
        if (pinnedPid != 0)
        {
            for (int i = 0; i < candidatePids.Count; i++)
                if (candidatePids[i] == pinnedPid) return i;
            return -1;   // pinned but absent: refuse, never fall back to a different device
        }
        return candidatePids.Count > 0 ? 0 : -1;
    }

    public void Dispose()
    {
        foreach (HidDevice h in _hid) h.Dispose();
        _hid.Clear();
    }
}
