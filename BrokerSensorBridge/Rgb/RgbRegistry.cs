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
                                    bool allowHidRgb, Action<string> log)
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
                        log($"[rgb] zone '{zone.Id}' -> HID PID 0x{hids[sel].ProductId:X4}"
                          + (zone.HidProductId != 0 ? " (pinned)" : " (unpinned — pin HidProductId in the profile)"));
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
