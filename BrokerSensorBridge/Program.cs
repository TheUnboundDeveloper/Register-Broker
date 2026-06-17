using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrokerSensorBridge;

internal static class Program
{
    private static BridgeConfig Config = new();

    public static async Task<int> Main(string[] args)
    {
        TrySetUtf8Output();

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return await RunControlSelfTestAsync();

#if BROKER_DEV_PROBES
        /*-----------------------------------------------------------*\
        | Raw hardware bring-up probes. These open \\.\BrokerSmbus    |
        | DIRECTLY (bypassing the broker) and take RAW bus/address/    |
        | command — they deliberately sidestep the named-catalog      |
        | "clients never supply an address" model, so they are        |
        | COMPILE-TIME GATED and excluded from a normal build. Enable  |
        | only for dev/bring-up with `dotnet build -p:DevProbes=true`. |
        \*-----------------------------------------------------------*/
        if (args.Any(a => a.Equals("--smbus-read", StringComparison.OrdinalIgnoreCase)))
            return RunSmbusProbe(args);

        if (args.Any(a => a.Equals("--smu-read", StringComparison.OrdinalIgnoreCase)))
            return RunSmuProbe(args);

        if (args.Any(a => a.Equals("--superio-read", StringComparison.OrdinalIgnoreCase)))
            return RunSuperioProbe(args);

        if (args.Any(a => a.Equals("--ene-read", StringComparison.OrdinalIgnoreCase)))
            return RunEneReadProbe(args);

        if (args.Any(a => a.Equals("--ene-set", StringComparison.OrdinalIgnoreCase)))
            return RunEneSetProbe(args);
#endif

        if (args.Any(a => a.Equals("--client", StringComparison.OrdinalIgnoreCase)))
            return await BrokerControlClient.RunAsync(args);

        /* Inspect the resolved board calibration exactly as the broker will serve it (board DMI,
           which files matched, and every channel's id/label/unit/availability). Offline — no pipe,
           no admin. Output is only visible when redirected (WinExe): use `--calibration > out.txt`. */
        if (args.Any(a => a.Equals("--calibration", StringComparison.OrdinalIgnoreCase) || a.Equals("--calib", StringComparison.OrdinalIgnoreCase)))
            return RunCalibrationInspect(args);

        /* RGB USB-HID discovery (read-only): enumerate HID interfaces under a USB vendor id and print
           PID + feature-report length, so the Mystic Light controller's PID can be pinned in the
           profile (RgbZone.HidProductId). No pipe, no writes, no service restart. Close vendor RGB
           apps (OpenRGB/MSI Center) first so the controller isn't being driven while you look.
           WinExe: redirect output (`--hid-scan > hid.txt`). */
        if (args.Any(a => a.Equals("--hid-scan", StringComparison.OrdinalIgnoreCase)))
            return RunHidScan(args);

        /*-----------------------------------------------------------*\
        | Windows-Service routing. When launched by the SCM (or with  |
        | an explicit --service flag from the installer's binPath),    |
        | host the long-running broker/control body under the Service  |
        | Control Manager so it gets SCM start/stop + graceful         |
        | shutdown. Interactive/dev invocations fall through to the    |
        | console path. The same RunBrokerAsync / RunControlServiceAsync|
        | body backs both, so there is one code path to reason about.  |
        \*-----------------------------------------------------------*/
        bool serviceMode = args.Any(a => a.Equals("--service", StringComparison.OrdinalIgnoreCase))
                        || Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();

        /* Separate RGB-control SERVICE: elevated, write-only, on its own pipe. */
        if (args.Any(a => a.Equals("--control", StringComparison.OrdinalIgnoreCase)))
        {
            return serviceMode
                ? await ServiceHost.RunControlServiceAsync(args)
                : await RunControlServiceAsync(args, ConsoleShutdownToken());
        }

        if (serviceMode)
            return await ServiceHost.RunSensorBrokerAsync(args);

        return await RunBrokerAsync(args, ConsoleShutdownToken());
    }

    /*-----------------------------------------------------------*\
    | Write console output as UTF-8 so the degree sign in unit    |
    | strings (e.g. "°C") survives redirection. We replace        |
    | Console.Out with a UTF-8 (no BOM) writer over the raw stdout |
    | stream — this works whether stdout is a console or a         |
    | redirected file, and avoids the OEM-codepage mojibake the    |
    | default writer produces. No-op (and swallowed) when there is |
    | no usable stdout, e.g. running headless as a service.        |
    \*-----------------------------------------------------------*/
    /*-----------------------------------------------------------*\
    | Loud marker emitted at startup (and in --selftest) when the |
    | raw hardware probes were compiled in (-p:DevProbes=true), so |
    | a dev build can never be mistaken for a deployment build.    |
    | In a normal build this compiles to a no-op.                  |
    \*-----------------------------------------------------------*/
    private static void WarnIfDevBuild(Action<string> sink)
    {
#if BROKER_DEV_PROBES
        sink("*****************************************************************************");
        sink("*** DEV BUILD — raw hardware probes (--smbus-read/--smu-read/--superio-read");
        sink("***            /--ene-read/--ene-set) are COMPILED IN. DO NOT DEPLOY.      ***");
        sink("*****************************************************************************");
#endif
    }

    private static void TrySetUtf8Output()
    {
        try
        {
            Stream stdout = Console.OpenStandardOutput();
            if (stdout == Stream.Null) return;
            var writer = new StreamWriter(stdout, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
            Console.SetOut(writer);
        }
        catch { /* no usable stdout (headless service); leave the default */ }
    }

    /// <summary>Production path of the optional user calibration override (ProgramData; read by the service).</summary>
    private static string UserCalibrationPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SensorBroker", "calibration.user.json");

    /*-----------------------------------------------------------*\
    | --calibration: print the resolved board calibration exactly |
    | as the broker will serve it (DMI identity, which files       |
    | matched, every channel's id/label/unit/availability). This   |
    | is the "does my override take effect?" check — offline, no    |
    | pipe, no admin. `--user=<path>` points at a custom override   |
    | so you can test one from anywhere; default is ProgramData.    |
    \*-----------------------------------------------------------*/
    /*-----------------------------------------------------------*\
    | RGB USB-HID discovery (read-only). Enumerates HID interfaces |
    | under a USB vendor id (default MSI 0x1462) and prints each    |
    | one's product id + feature-report length + path. Use it to    |
    | find the Mystic Light controller's PID, then pin it in the    |
    | profile (RgbZone.HidProductId). Non-destructive: no writes,    |
    | no pipe, no service restart. Close OpenRGB / MSI Center first  |
    | so nothing is fighting for the device.                        |
    |   BrokerSensorBridge.exe --hid-scan [--vid=1462] > hid.txt    |
    \*-----------------------------------------------------------*/
    private static int RunHidScan(string[] args)
    {
        string vidArg = args.FirstOrDefault(a => a.StartsWith("--vid=", StringComparison.OrdinalIgnoreCase))?["--vid=".Length..] ?? "1462";
        if (!ushort.TryParse(vidArg, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ushort vid))
        {
            Console.WriteLine($"bad --vid (expected hex, e.g. 1462): '{vidArg}'");
            return 2;
        }

        Console.WriteLine($"Scanning HID interfaces at VID 0x{vid:X4}{(vid == 0x1462 ? " (MSI / Mystic Light)" : "")} ...");
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(vid, Console.WriteLine);
        if (devs.Count == 0)
        {
            Console.WriteLine("No matching HID interfaces. Is the board's RGB controller present? Try a different --vid,");
            Console.WriteLine("or check that a vendor RGB app isn't holding the device exclusively.");
            return 1;
        }

        Console.WriteLine($"Found {devs.Count} interface(s) at VID 0x{vid:X4}:");
        foreach (HidDevice d in devs)
            Console.WriteLine($"  PID 0x{d.ProductId:X4}  featureReportLen={d.FeatureReportByteLength,-4}  {d.Path}");
        Console.WriteLine();
        Console.WriteLine("Pin the RGB controller's PID via HidProductId in the board's RgbBoardProfile (RgbCatalog.cs),");
        Console.WriteLine("then rebuild the broker only. featureReportLen identifies the Mystic Light variant (185/162/112).");
        foreach (HidDevice d in devs) d.Dispose();
        return 0;
    }

    private static int RunCalibrationInspect(string[] args)
    {
        BoardIdentity board = BoardIdentity.Detect();
        string defaultPath = Path.Combine(AppContext.BaseDirectory, "calibration.default.json");
        string? userArg = args.FirstOrDefault(a => a.StartsWith("--user=", StringComparison.OrdinalIgnoreCase));
        string userPath = userArg != null ? userArg.Substring("--user=".Length) : UserCalibrationPath();

        Console.WriteLine($"Board identity (DMI) : {board}");
        Console.WriteLine($"Default calibration  : {defaultPath} ({(File.Exists(defaultPath) ? "present" : "MISSING")})");
        Console.WriteLine($"User override        : {userPath} ({(File.Exists(userPath) ? "present" : "absent")})");
        Console.WriteLine();

        CalibrationStore store = CalibrationStore.Load(board, Console.WriteLine, defaultPath, userPath);
        SensorCatalog.Configure(store);

        using var smbus = new SmbusDriverBackend(_ => { });   // live availability if the driver is loaded
        Console.WriteLine();
        Console.WriteLine($"Resolved catalog ({SensorCatalog.All.Count} channels) — labels/scales as served:");
        Console.WriteLine($"  {"raw id",-20} {"label",-30} {"unit",-4} avail");
        Console.WriteLine("  " + new string('-', 64));
        foreach (SensorCatalogEntry e in SensorCatalog.All)
            Console.WriteLine($"  {e.Id,-20} {e.Label,-30} {e.Unit,-4} {(e.IsAvailable(smbus) ? "yes" : "")}");
        return 0;
    }

    /*-----------------------------------------------------------*\
    | Interactive shutdown: a CTS that fires on Ctrl+C, so a      |
    | console run shuts down as gracefully as a service stop.     |
    \*-----------------------------------------------------------*/
    private static CancellationToken ConsoleShutdownToken()
    {
        var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;            // shut down gracefully instead of hard-killing
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;
        // Drop the static event subscription once shutdown fires so the handler (and the CTS it
        // captures) don't outlive their use on the long-lived Console.CancelKeyPress event.
        cts.Token.Register(() => Console.CancelKeyPress -= handler);
        return cts.Token;
    }

    /*-----------------------------------------------------------*\
    | The sensor-broker body. Runs the LHM poll loop and the      |
    | authenticated control channel (named pipe) until            |
    | externalToken is cancelled (SCM stop / Ctrl+C) or the       |
    | control loop exits. The driver handle is disposed in        |
    | finally — that releases \\.\BrokerSmbus so the kernel       |
    | service can unload on stop.                                  |
    \*-----------------------------------------------------------*/
    internal static async Task<int> RunBrokerAsync(string[] args, CancellationToken externalToken)
    {
        SmbusDriverBackend? smbus = null;
        try
        {
            Config = BridgeConfig.Load(args, Log);
            Directory.CreateDirectory(Path.GetDirectoryName(Config.LogFileExpanded) ?? ".");
            Log("BrokerSensorBridge starting.");
            WarnIfDevBuild(Log);
            Log($"Process elevated: {IsElevated()}");

            /* Link to the external token (SCM stop / Ctrl+C). Cancelling either, or the
               control loop returning first, tears the whole broker down cleanly. */
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

            /*-----------------------------------------------------------*\
            | Authenticated control channel (well-known named pipe) —     |
            | the broker's only serving surface. Authentication is by     |
            | pipe DACL + peer-process identity + Authenticode signer pin |
            | (no shared secret, no descriptor).                          |
            \*-----------------------------------------------------------*/
            Log($@"Control channel pipe: \\.\pipe\{BrokerProtocol.PipeName} (protocol v{BrokerProtocol.Version})");
            Log($"Authorized-client enforcement: {(Config.RequireAuthorizedClient ? "ON" : "off (audit only)")}");
            if (Config.AllowedClientPaths.Length > 0)   Log($"Allowed client paths: {string.Join(", ", Config.AllowedClientPaths)}");
            if (Config.AllowedClientSigners.Length > 0) Log($"Allowed client signers: {string.Join(", ", Config.AllowedClientSigners)}");
            var authorization = new ClientAuthorization(
                Config.RequireAuthorizedClient, Config.AllowedClientPaths, Config.AllowedClientSigners, Log);
            smbus = new SmbusDriverBackend(Log);
            Log($"SMBus control: {smbus.Describe}");

            /*-- Board calibration: map raw chip channels -> labels/scales by board DMI. Data-only
                  (no addresses). The baked default is layered first, then an optional user override
                  (ProgramData) on top — last file wins, so a user can relabel without a rebuild. --*/
            BoardIdentity board = BoardIdentity.Detect();
            Log($"[calib] board: {board}");
            string defaultCalibPath = Path.Combine(AppContext.BaseDirectory, "calibration.default.json");
            string userCalibPath = UserCalibrationPath();
            Log(File.Exists(userCalibPath)
                ? $"[calib] user override present: {userCalibPath}"
                : $"[calib] no user override (optional): {userCalibPath}");
            SensorCatalog.Configure(CalibrationStore.Load(board, Log, defaultCalibPath, userCalibPath));

            /*-----------------------------------------------------------*\
            | One-shot mode: print a single named-catalog snapshot, read  |
            | straight from the driver, and exit. Useful for diagnostics. |
            \*-----------------------------------------------------------*/
            if (args.Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine(BuildCatalogSnapshotJson(smbus));
                return 0;
            }

            var policy = new BrokerPolicy(Config.MaxOpsPerSecond, Config.RateBurst, Config.MaxSessions, Config.MaxSessionsPerIdentity);
            Log($"Control policy: {Config.MaxOpsPerSecond} ops/s (burst {Config.RateBurst}), max {Config.MaxSessions} sessions ({Config.MaxSessionsPerIdentity}/identity); audit -> {Config.AuditLogFileExpanded}");
            Audit("Broker starting; control channel up.");
            var control = new BrokerControlServer(
                BrokerProtocol.PipeName,
                Log,
                authorization,
                smbus,
                Audit,
                policy);
            Task controlLoop = control.RunAsync(shutdown.Token);

            /* Block until shutdown is requested (SCM stop / Ctrl+C) or the control loop exits
               on its own; then tear the rest down. The idle task is the wait that keeps the
               broker running forever; the ContinueWith swallows its cancellation so it isn't
               left as an unobserved exception. Sensors are served only over the named pipe
               (the `sensor.list` / `sensor.read` / `sensor.readall` ops) — there is no TCP
               surface and no bulk third-party-monitor payload. */
            Task idle = Task.Delay(Timeout.Infinite, shutdown.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            await Task.WhenAny(controlLoop, idle);
            shutdown.Cancel();
            await controlLoop;
            return 0;
        }
        catch (Exception ex)
        {
            Log("FATAL: " + ex);
            return 1;
        }
        finally
        {
            smbus?.Dispose();
            Log("BrokerSensorBridge exiting.");
        }
    }

    /*-----------------------------------------------------------*\
    | --once: serialize every currently-available catalog sensor  |
    | (id/label/value/unit) as one JSON object — the named-catalog |
    | equivalent of the old one-shot reading.                      |
    \*-----------------------------------------------------------*/
    private static string BuildCatalogSnapshotJson(ISmbusBackend smbus)
    {
        var items = new List<object>();
        foreach (SensorCatalogEntry e in SensorCatalog.Available(smbus))
        {
            SensorReading r = e.Read(smbus);
            items.Add(new { id = e.Id, label = e.Label, unit = e.Unit, ok = r.Ok, value = r.Ok ? r.Value : (double?)null, status = r.Status });
        }
        var snapshot = new
        {
            source = "BrokerSensorBridge",
            timestampUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            elevated = IsElevated(),
            sensors = items
        };
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
    }

    /*-----------------------------------------------------------*\
    | Self-test: exercises the control-channel handshake and      |
    | scope enforcement in-process, with no hardware and no HTTP  |
    | bind, so the auth path can be verified on any machine via   |
    |   BrokerSensorBridge.exe --selftest                        |
    \*-----------------------------------------------------------*/
#if BROKER_DEV_PROBES
    /*-----------------------------------------------------------*\
    | Dev bring-up probe: talks straight to the BrokerSmbus      |
    | driver (bypassing the handshake/broker) so the kernel read  |
    | path can be validated in isolation. The driver's in-kernel  |
    | guard still applies.                                        |
    |   BrokerSensorBridge.exe --smbus-read --bus=0 --addr=0x50  |
    |        --cmd=0x00 [--size=byte|word|block] [--len=N]        |
    \*-----------------------------------------------------------*/
    private static int RunSmbusProbe(string[] args)
    {
        int bus  = ProbeArgInt(args, "--bus=", 0);
        int addr = ProbeArgInt(args, "--addr=", -1);
        int cmd  = ProbeArgInt(args, "--cmd=", 0);
        int len  = ProbeArgInt(args, "--len=", 1);
        string size = ProbeArgStr(args, "--size=", "byte");

        if (addr < 0)
        {
            Console.WriteLine("usage: --smbus-read --bus=0 --addr=0x50 --cmd=0x00 [--size=byte|word|block] [--len=N]");
            return 2;
        }

        SmbusOp op = size.ToLowerInvariant() switch
        {
            "word"  => SmbusOp.ReadWord,
            "block" => SmbusOp.ReadBlock,
            _       => SmbusOp.ReadByte
        };

        using var backend = new SmbusDriverBackend(Console.WriteLine);
        if (!backend.Available)
        {
            Console.WriteLine("SMBus driver not available: " + backend.Describe);
            return 1;
        }

        SmbusResult r = backend.Read(bus, addr, cmd, op, len);
        Console.WriteLine(r.Ok
            ? $"OK  bus={bus} addr=0x{addr:X2} cmd=0x{cmd:X2} {op} -> {Convert.ToHexString(r.Data)}"
            : $"FAIL status={r.Status}");
        return r.Ok ? 0 : 1;
    }

    /*-----------------------------------------------------------*\
    | Dev bring-up probe for the AMD SMU CPU-temperature path.    |
    | Opens the driver directly, reads the raw reported-temp      |
    | register, and applies the k10temp decode in user mode.      |
    |   BrokerSensorBridge.exe --smu-read                        |
    \*-----------------------------------------------------------*/
    private static int RunSmuProbe(string[] args)
    {
        double offset = ProbeArgDouble(args, "--tctl-offset=", 0.0);   // Tctl->Tdie, per-SKU

        using var backend = new SmbusDriverBackend(Console.WriteLine);
        if (!backend.SmuAvailable)
        {
            Console.WriteLine("SMU CPU-temp path not available: " + backend.Describe);
            return 1;
        }

        if (!backend.TryReadSmuRaw((uint)0 /* BrokerSmuCpuTemp */, out uint raw, out SmbusStatus st))
        {
            Console.WriteLine($"SMU read failed: status={st}");
            return 1;
        }

        /* Shared k10temp decode (see SensorCatalog). Tdie = Tctl minus a per-SKU
           offset (0 on most desktop Ryzen, e.g. Vermeer/5800X3D). */
        double tctl = SensorCatalog.DecodeAmdCpuTctlC(raw);
        double tdie = SensorCatalog.DecodeAmdCpuTctlC(raw, offset);

        Console.WriteLine($"OK  raw=0x{raw:X8}  Tctl={tctl:F2} C" +
                          (offset != 0.0 ? $"  Tdie={tdie:F2} C (offset {offset:F1})" : "  (Tdie=Tctl, offset 0)"));

        /* Per-CCD die temps (k10temp ZEN_CCD_TEMP). Only CCDs with the valid bit (0x800) set
           are real; compare to HWiNFO "CPU CCDn (Tdie)". HWiNFO numbers CCDs 1-based. */
        Console.WriteLine("Per-CCD die temps (valid bit 0x800):");
        for (uint c = 0; c < 8; c++)
        {
            if (backend.TryReadSmuRaw(1 /* BrokerSmuCcd0Temp */ + c, out uint craw, out SmbusStatus cst))
            {
                bool valid = (craw & 0x800u) != 0;
                Console.WriteLine($"  ccd{c}  raw=0x{craw:X8}  " +
                    (valid ? $"{SensorCatalog.DecodeAmdCcdTempC(craw):F2} C" : "(not valid / not present)"));
            }
            else Console.WriteLine($"  ccd{c}  ({cst})");
        }
        return 0;
    }

    /*-----------------------------------------------------------*\
    | Dev bring-up probe for the NCT6687D Super-I/O path. Walks    |
    | all temp/fan indices and decodes them so they can be mapped  |
    | to board labels against HWiNFO/LHM.                         |
    |   BrokerSensorBridge.exe --superio-read                    |
    \*-----------------------------------------------------------*/
    private static int RunSuperioProbe(string[] args)
    {
        using var backend = new SmbusDriverBackend(Console.WriteLine);
        if (!backend.SuperioAvailable)
        {
            Console.WriteLine("Super-I/O (NCT6687D) not available: " + backend.Describe);
            return 1;
        }

        Console.WriteLine("Temperatures (index -> C):");
        for (uint i = 0; i < 7; i++)
        {
            if (backend.TryReadSuperioRaw(0 /* Temp */, i, out uint raw, out SmbusStatus st))
            {
                sbyte value = (sbyte)(raw & 0xFF);
                int half = (int)((raw >> 8) >> 7) & 1;
                Console.WriteLine($"  temp{i}  raw=0x{raw:X4}  {value + 0.5 * half,5:F1} C");
            }
            else Console.WriteLine($"  temp{i}  ({st})");
        }

        Console.WriteLine("Fans (index -> RPM):");
        for (uint i = 0; i < 8; i++)
        {
            if (backend.TryReadSuperioRaw(1 /* Fan */, i, out uint raw, out SmbusStatus st))
                Console.WriteLine($"  fan{i}   raw=0x{raw:X4}  {raw & 0xFFFF,5} RPM");
            else Console.WriteLine($"  fan{i}   ({st})");
        }

        /* Voltage bank. "pin" is the ADC reading in volts (high*16 + low>>4 mV, per nct6687d);
           the catalog applies the per-rail divider multiplier (×12 for +12V, ×5 for +5V, ×2 for
           DRAM). Indices 0–13 are defined rails; 14–15 are unused on the NCT6687. */
        Console.WriteLine("Voltages (index -> pin volts; catalog applies per-rail multiplier):");
        for (uint i = 0; i < 16; i++)
        {
            if (backend.TryReadSuperioRaw(2 /* Voltage */, i, out uint raw, out SmbusStatus st))
            {
                int mv = (int)((raw >> 8) & 0xFF) * 16 + ((int)(raw & 0xFF) >> 4);
                Console.WriteLine($"  volt{i,-2} raw=0x{raw:X4}  pin={mv / 1000.0,6:F3} V");
            }
            else Console.WriteLine($"  volt{i,-2} ({st})");
        }
        return 0;
    }

    /*-----------------------------------------------------------*\
    | Stage 1 RGB bring-up: NON-DESTRUCTIVE. Verifies the kernel   |
    | write path + brick-guard and the ENE register protocol by    |
    | (a) confirming a write to SPD (0x50) is Forbidden, and        |
    | (b) reading the ENE device-name string from 0x70/0x71. No     |
    | LED register is touched.                                      |
    |   BrokerSensorBridge.exe --ene-read                         |
    \*-----------------------------------------------------------*/
    private static int RunEneReadProbe(string[] args)
    {
        using var backend = new SmbusDriverBackend(Console.WriteLine);
        if (!backend.WriteAvailable)
        {
            Console.WriteLine("SMBus write path not available: " + backend.Describe);
            return 1;
        }

        /* Brick-guard proof: a write to SPD must be refused in-kernel. */
        backend.TryWrite(0, 0x50, 0x00, 0x00, word: false, out SmbusStatus spd);
        Console.WriteLine($"brick-guard: write to SPD 0x50 -> {spd}  (expect Forbidden)");

        /* DDR4 SPD page reset. The SPD is 512 bytes across two 256-byte pages selected
           by addressing 0x36 (SPA0 -> page 0) or 0x37 (SPA1 -> page 1). A broad scan that
           touches 0x37 latches the DIMMs to page 1 (persists across warm reboot), which
           makes page-0 reads return wrong data. Address 0x36 to force page 0 first. */
        backend.Read(0, 0x36, 0x00, SmbusOp.ReadByte, 1);    // SPA0 -> page 0

        /* Canary: SPD byte 0x02 is a constant 0x0C (DDR4). After the page reset this must
           hold; if not, the SMBus really is being disturbed by another master. */
        SmbusResult spdRead = backend.Read(0, 0x50, 0x02, SmbusOp.ReadByte, 1);
        byte spdVal = spdRead.Ok && spdRead.Data.Length > 0 ? spdRead.Data[0] : (byte)0xFF;
        Console.WriteLine($"canary: SPD bus0 0x50 cmd0x02 -> {spdRead.Status} val=0x{spdVal:X2}  (must be 0x0C)");
        if (!(spdRead.Ok && spdVal == 0x0C))
        {
            Console.WriteLine("ABORT: canary != 0x0C even after SPD page-0 reset. Close any RGB/monitoring tools (HWiNFO etc.) /");
            Console.WriteLine("       any vendor RGB app, then retry.");
            return 1;
        }

        /* Primary SMBus address map (read_byte cmd 0x00). Secondary controller is
           deferred until proper detection lands (see driver note). */
        /* Show the I/O base + mux each bus uses, so we can confirm the secondary is 0x0B20. */
        Console.WriteLine("Bus map (base : mux):");
        for (int bus = 0; bus < 8; bus++)
        {
            uint bi = backend.BusInfo[bus];
            if (bi != 0) Console.WriteLine($"  bus {bus}: base=0x{bi & 0xFFFF:X4} mux={(bi >> 16) & 0xFF}");
        }

        /* Primary bus scan. Skip SPA0/SPA1 so we never flip the SPD page. */
        Console.WriteLine("SMBus address scan (read_byte cmd 0x00):");
        for (int bus = 0; bus < 4; bus++)
        {
            var found = new List<string>();
            for (int a = 0x08; a <= 0x77; a++)
            {
                if (a == 0x36 || a == 0x37) continue;   // SPA0/SPA1: addressing these flips the DDR4 SPD page
                SmbusResult r = backend.Read(bus, a, 0x00, SmbusOp.ReadByte, 1);
                if (r.Ok) found.Add($"0x{a:X2}={(r.Data.Length > 0 ? r.Data[0] : 0):X2}");
            }
            Console.WriteLine($"  bus {bus}: {(found.Count > 0 ? string.Join(" ", found) : "(none)")}");
        }

        /* ENE identity read on bus 0 at the DRAM RGB addresses this board reports (0x39/0x3A). */
        Console.WriteLine("ENE device-name read (bus 0, 0x39/0x3A):");
        foreach (int addr in new[] { 0x39, 0x3A })
        {
            var ene = new EneController(backend, 0, addr);
            Console.WriteLine($"  0x{addr:X2}: \"{ene.ReadDeviceName()}\"");
        }
        return 0;
    }

    /*-----------------------------------------------------------*\
    | Stage 2 RGB bring-up: WRITE a color to the ENE DRAM at      |
    | 0x39/0x3A and commit. Watch the RAM change.                 |
    |   BrokerSensorBridge.exe --ene-set --color=00FF00 [--leds=5]|
    \*-----------------------------------------------------------*/
    private static int RunEneSetProbe(string[] args)
    {
        string colorHex = ProbeArgStr(args, "--color=", "FF0000").TrimStart('#');
        int    ledCount = ProbeArgInt(args, "--leds=", 5);
        if (colorHex.Length != 6)
        {
            Console.WriteLine("usage: --ene-set --color=RRGGBB [--leds=N]");
            return 2;
        }

        byte r, g, b;
        try
        {
            r = Convert.ToByte(colorHex.Substring(0, 2), 16);
            g = Convert.ToByte(colorHex.Substring(2, 2), 16);
            b = Convert.ToByte(colorHex.Substring(4, 2), 16);
        }
        catch { Console.WriteLine("bad --color (expected RRGGBB hex)"); return 2; }

        using var backend = new SmbusDriverBackend(Console.WriteLine);
        if (!backend.WriteAvailable)
        {
            Console.WriteLine("SMBus write path not available: " + backend.Describe);
            return 1;
        }

        backend.Read(0, 0x36, 0x00, SmbusOp.ReadByte, 1);   // SPA0 -> page 0 (hygiene)

        foreach (int addr in new[] { 0x39, 0x3A })
        {
            var ene = new EneController(backend, 0, addr);
            bool ok = ene.SetAllDirect(r, g, b, ledCount);
            Console.WriteLine($"0x{addr:X2}: set #{colorHex} across {ledCount} LEDs -> {(ok ? "OK" : "FAILED")}");
        }
        return 0;
    }

    private static double ProbeArgDouble(string[] args, string prefix, double fallback)
    {
        string s = ProbeArgStr(args, prefix, "");
        return double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;
    }

    private static string ProbeArgStr(string[] args, string prefix, string fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return a is null ? fallback : a[prefix.Length..];
    }

    private static int ProbeArgInt(string[] args, string prefix, int fallback)
    {
        string s = ProbeArgStr(args, prefix, "");
        if (string.IsNullOrEmpty(s)) return fallback;
        try
        {
            return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt32(s, 16)
                : int.Parse(s);
        }
        catch { return fallback; }
    }
#endif // BROKER_DEV_PROBES

    /*-----------------------------------------------------------*\
    | Separate RGB-control service. Elevated, write-only: holds   |
    | the driver's brick-guarded write IOCTL and serves the       |
    | named `rgb.set {device,color}` op (rgb:write scope) on its  |
    | own pipe \\.\pipe\BrokerControl, so the sensor broker never |
    | gains write capability. Non-admin clients drive RGB through  |
    | it (catalog-only — never an address).                        |
    \*-----------------------------------------------------------*/
    internal static async Task<int> RunControlServiceAsync(string[] args, CancellationToken externalToken)
    {
        Config = BridgeConfig.Load(args, Log);
        Directory.CreateDirectory(Path.GetDirectoryName(Config.LogFileExpanded) ?? ".");
        Log("Register Broker RGB control service starting.");
        WarnIfDevBuild(Log);
        Log($"Process elevated: {IsElevated()}");

        using var smbus = new SmbusDriverBackend(Log);
        Log($"SMBus control: {smbus.Describe}");
        if (smbus.WriteAvailable)
            Log("SMBus control: block-write capable (per-LED frames go out as atomic 3-byte blocks).");

        if (smbus.SuperioRgbAvailable)
            Log("SMBus control: NCT6687 EC RGB write path available (motherboard 12V header).");

        /* Build the RGB registry from the DMI-matched board profile crossed with the transports
           present: ENE/Aura DRAM (CAP_WRITE), the NCT6687 EC 12V header (CAP_SUPERIO_RGB, inert
           until HW-validated), and — when AllowHidRgb is set — the MSI Mystic Light USB-HID path
           for addressable headers. Per-zone labels come from calibration (addresses-free). */
        BoardIdentity board = BoardIdentity.Detect();
        Log($"Board identity (DMI): {board}");
        CalibrationStore calib = CalibrationStore.Load(board, Log,
            Path.Combine(AppContext.BaseDirectory, "calibration.default.json"), UserCalibrationPath());
        if (Config.AllowHidRgb)
            Log("RGB: USB-HID (Mystic Light) transport ENABLED (AllowHidRgb) — reduced assurance, no kernel brick-guard.");

        using var rgb = RgbRegistry.Build(smbus, board, calib, Config.AllowHidRgb, Log);
        if (!rgb.Any)
        {
            Log("Control service: no RGB devices found (no write transport available). Nothing to serve.");
            return 2;
        }

        var authorization = new ClientAuthorization(
            Config.RequireAuthorizedClient, Config.AllowedClientPaths, Config.AllowedClientSigners, Log);
        /* RGB frame updates (effects) come much faster than the 30 ops/s sensor default, so the
           control service runs a higher rate ceiling (the SMBus write speed is the real cap).
           Respects a higher value if configured. */
        var policy = new BrokerPolicy(
            Math.Max(Config.MaxOpsPerSecond, 120.0), Math.Max(Config.RateBurst, 240.0), Config.MaxSessions, Config.MaxSessionsPerIdentity);
        Log($@"Control channel pipe: \\.\pipe\{BrokerProtocol.ControlPipeName} (rgb:write enabled)");
        Log($"Authorized-client enforcement: {(Config.RequireAuthorizedClient ? "ON" : "off (audit only)")}");
        Audit("Control service starting (rgb:write).");

        var control = new BrokerControlServer(
            BrokerProtocol.ControlPipeName,
            Log, authorization, smbus, Audit, policy, allowRgbWrite: true, rgb: rgb);

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        await control.RunAsync(shutdown.Token);
        return 0;
    }

    private static async Task<int> RunControlSelfTestAsync()
    {
        WarnIfDevBuild(Console.WriteLine);
        using var cts = new CancellationTokenSource();
        int failures = 0;

        var noSmbus = new MockSmbusBackend(false);

        /*-- Signature plumbing: PeerSignature against a known-signed system binary
              and a non-signed path, so the WinVerifyTrust path is exercised even
              though this dev exe may itself be unsigned. --*/
        failures += SelfTestSignatureChecks();

        /*-- Calibration regression: the data-driven catalog must reproduce the MSI NCT6687D map
              (labels + scales) and resolve legacy ids via aliases (CALIBRATION-AND-REGISTRY-PLAN). --*/
        failures += SelfTestCalibration();

        /*-- Chipset gates: NCT668x EC family ids light the EC channels, NCT6775-family ids
              light theirs, the two are mutually exclusive, and the NCT6775 decode math holds
              (docs/SUPERIO-NCT6683-NCT6686.md / SUPERIO-NCT6775-FAMILY.md). --*/
        failures += SelfTestChipsetGates();

        /*-- Registry integrity: the channel registry validates (unique names/ids, prefix
              ownership), every channel has decoder coverage, and the broker registry's
              driver-backend names match what the (mock) driver enumerates — the contract
              that keeps the broker and kernel tables from drifting apart. --*/
        failures += SelfTestBackendRegistry();

        /*-- RGB catalog: board-aware zone profiles validate, the MSI profile resolves the full
              zone vocabulary, the generic fallback is DRAM-only, and label overrides + transport
              gating build the expected device set. --*/
        failures += SelfTestRgbCatalog();

        /*-- Server 1: enforcement OFF (audit only) -- hello & scope cases --*/
        const string pipeAudit = "SensorBrokerTest.audit";
        var auditAuth = new ClientAuthorization(false, null, null, m => Console.WriteLine("[audit-srv] " + m));
        var srvAudit = new BrokerControlServer(pipeAudit, m => Console.WriteLine("[audit-srv] " + m), auditAuth, noSmbus);
        Task t1 = srvAudit.RunAsync(cts.Token);

        failures += await SelfTestCase("valid hello -> sensor.readall data", pipeAudit, scopes: new[] { "sensors:read" }, op: "sensor.readall", expect: "data");
        failures += await SelfTestCase("ungranted scope -> deny",            pipeAudit, scopes: new[] { "smbus:write" },  op: "sensor.readall", expect: "deny");

        /*-- Server 2: enforcement ON, allowlist = this process path -> should proceed --*/
        string selfPath = Environment.ProcessPath ?? "";
        const string pipeAllow = "SensorBrokerTest.allow";
        var allowAuth = new ClientAuthorization(true, new[] { selfPath }, null, m => Console.WriteLine("[allow-srv] " + m));
        var srvAllow = new BrokerControlServer(pipeAllow, m => Console.WriteLine("[allow-srv] " + m), allowAuth, noSmbus);
        Task t2 = srvAllow.RunAsync(cts.Token);
        failures += await SelfTestCase("authorized client path -> data", pipeAllow, scopes: new[] { "sensors:read" }, op: "sensor.readall", expect: "data");

        /*-- Server 3: enforcement ON, allowlist excludes us -> dropped at connect --*/
        const string pipeDeny = "SensorBrokerTest.deny";
        var denyAuth = new ClientAuthorization(true, new[] { @"C:\does\not\exist.exe" }, null, m => Console.WriteLine("[deny-srv] " + m));
        var srvDeny = new BrokerControlServer(pipeDeny, m => Console.WriteLine("[deny-srv] " + m), denyAuth, noSmbus);
        Task t3 = srvDeny.RunAsync(cts.Token);
        failures += await SelfTestRejectedCase("unauthorized client -> connection closed", pipeDeny);

        /*-- Server 4: SMU sensor available (mock) -> catalog cpu.temp reads; unknown id denies --*/
        const string pipeSensor = "SensorBrokerTest.sensor";
        var sensorAuth = new ClientAuthorization(false, null, null, m => Console.WriteLine("[sensor-srv] " + m));
        var srvSensor = new BrokerControlServer(pipeSensor, m => Console.WriteLine("[sensor-srv] " + m), sensorAuth, new MockSmbusBackend(available: true, smuAvailable: true));
        Task t4 = srvSensor.RunAsync(cts.Token);
        failures += await SelfTestCase("sensor.read cpu.temp -> data",   pipeSensor, scopes: new[] { "sensors:read" }, op: "sensor.read", expect: "data", id: "cpu.temp");
        failures += await SelfTestCase("sensor.read unknown id -> deny",  pipeSensor, scopes: new[] { "sensors:read" }, op: "sensor.read", expect: "deny", id: "no.such.sensor");

        /*-- Server 5: no SMU -> cpu.temp not available -> sensor.read denied (no oracle) --*/
        const string pipeNoSmu = "SensorBrokerTest.nosmu";
        var noSmuAuth = new ClientAuthorization(false, null, null, m => Console.WriteLine("[nosmu-srv] " + m));
        var srvNoSmu = new BrokerControlServer(pipeNoSmu, m => Console.WriteLine("[nosmu-srv] " + m), noSmuAuth, new MockSmbusBackend(available: true, smuAvailable: false));
        Task t5 = srvNoSmu.RunAsync(cts.Token);
        failures += await SelfTestCase("sensor.read without SMU -> deny", pipeNoSmu, scopes: new[] { "sensors:read" }, op: "sensor.read", expect: "deny", id: "cpu.temp");

        /*-- Server 6: tiny rate policy (1 op/s, burst 3) -> a flood is throttled --*/
        const string pipeRate = "SensorBrokerTest.rate";
        var rateAuth = new ClientAuthorization(false, null, null, _ => { });
        var srvRate = new BrokerControlServer(pipeRate, _ => { }, rateAuth,
                                              new MockSmbusBackend(true), _ => { }, new BrokerPolicy(1.0, 3.0, 32, 8));
        Task t6 = srvRate.RunAsync(cts.Token);
        failures += await SelfTestRateLimitCase("rate limiting throttles a flood", pipeRate);

        cts.Cancel();
        try { await Task.WhenAll(t1, t2, t3, t4, t5, t6); } catch { /* ignore shutdown noise */ }

        Console.WriteLine(failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }

    /*-----------------------------------------------------------*\
    | Exercises PeerSignature: a Microsoft-signed OS binary must  |
    | verify and yield a thumbprint; a non-signed/garbage path    |
    | must not.                                                   |
    \*-----------------------------------------------------------*/
    private static int SelfTestSignatureChecks()
    {
        int failures = 0;

        string signed = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        bool okSigned = PeerSignature.TryGetSigner(signed, out string? thumb, out string? thumb256, out string? subject, out bool trusted);
        bool pass1 = okSigned && !string.IsNullOrEmpty(thumb);
        Console.WriteLine($"  [{(pass1 ? "PASS" : "FAIL")}] signature: {Path.GetFileName(signed)} -> signed={okSigned} trusted={trusted} sha1={thumb} subject={subject}");
        if (!pass1) failures++;

        // SHA-256 thumbprint must also be produced (40 hex for SHA-1, 64 for SHA-256) so a
        // signer allowlist can pin on the stronger hash.
        bool pass1b = okSigned && (thumb256?.Length == 64) && (thumb?.Length == 40);
        Console.WriteLine($"  [{(pass1b ? "PASS" : "FAIL")}] signature: dual thumbprint -> sha256={thumb256}");
        if (!pass1b) failures++;

        string bogus = Path.Combine(Path.GetTempPath(), "definitely-not-a-signed-binary.zzz");
        bool okBogus = PeerSignature.TryGetSigner(bogus, out _, out _, out _, out _);
        bool pass2 = !okBogus;
        Console.WriteLine($"  [{(pass2 ? "PASS" : "FAIL")}] signature: missing path -> signed={okBogus} (expected false)");
        if (!pass2) failures++;

        return failures;
    }

    /*-----------------------------------------------------------*\
    | Calibration regression gate. Loads the shipped default      |
    | calibration, forces the MSI board identity, and asserts the |
    | data-driven catalog reproduces the validated NCT6687D map:  |
    |   * legacy ids resolve via alias to stable raw ids,         |
    |   * labels come from the board entry,                       |
    |   * the per-rail scale is applied to the decoded value.     |
    | Restores the default (builtin) store so later cases are     |
    | unaffected.                                                 |
    \*-----------------------------------------------------------*/
    private static int SelfTestCalibration()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] calib: {name}");
            if (!ok) failures++;
        }

        try
        {
            string calibPath = Path.Combine(AppContext.BaseDirectory, "calibration.default.json");
            // Force the MSI identity so the board entry matches regardless of the test host's DMI
            // (must equal the default file's exact match).
            var msi = new BoardIdentity("Micro-Star International Co., Ltd.", "MPG B550I GAMING EDGE MAX WIFI (MS-7C92)");
            SensorCatalog.Configure(CalibrationStore.Load(calibPath, msi, _ => { }));

            // Mock NCT board: every Super-I/O read returns 0x4000 -> NctVoltageMv = 1024 mV = 1.024 V base.
            var nct = new MockSmbusBackend(available: true, superioAvailable: true, superioChipId: 0xD592, superioRaw: 0x4000);

            // Alias -> raw id, board label, and the x12 scale (1.024 V * 12 = 12.288 V).
            SensorCatalogEntry? v12 = SensorCatalog.Find("board.12v.volt");
            Check("legacy id board.12v.volt resolves", v12 != null);
            Check("maps to raw id nct6687d.volt.0", v12?.Id == "nct6687d.volt.0");
            Check("board label '+12V'", v12?.Label == "+12V");
            SensorReading r = v12!.Read(nct);
            Check("x12 scale applied (12.288 V)", r.Ok && Math.Abs(r.Value - 12.288) < 1e-9);

            // Direct raw-id lookup works too.
            Check("raw id nct6687d.volt.0 resolves", SensorCatalog.Find("nct6687d.volt.0") != null);

            // A temp alias picks up its board label.
            SensorCatalogEntry? chipset = SensorCatalog.Find("board.chipset.temp");
            Check("board.chipset.temp -> nct6687d.temp.3 'Chipset Temperature'",
                  chipset?.Id == "nct6687d.temp.3" && chipset.Label == "Chipset Temperature");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] calib: threw {ex.Message}");
            failures++;
        }
        finally
        {
            SensorCatalog.Configure(CalibrationStore.Builtin);   // restore default for later cases
        }

        return failures;
    }

    /*-----------------------------------------------------------*\
    | Chipset-gate regression. Asserts the catalog's chip-family  |
    | gates and the NCT6775 decode math:                          |
    |   * NCT6683 (0xC731) / NCT6686 (0xD441) light the EC         |
    |     channels (same path as the validated NCT6687D),         |
    |   * an EC chip does NOT light the nct6775.* channels and     |
    |     vice versa (the families are mutually exclusive),       |
    |   * NCT6798 (0xD428): volt 0x64 -> 0.800 V (byte x 8 mV),   |
    |     temp 0x8064 -> 100.5 C (signed byte + half bit).        |
    \*-----------------------------------------------------------*/
    private static int SelfTestChipsetGates()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] chipset: {name}");
            if (!ok) failures++;
        }

        try
        {
            var nct6683 = new MockSmbusBackend(available: true, superioAvailable: true, superioChipId: 0xC731, superioRaw: 0x4000);
            var nct6686 = new MockSmbusBackend(available: true, superioAvailable: true, superioChipId: 0xD441, superioRaw: 0x4000);
            var nct6798 = new MockSmbusBackend(available: true, superioAvailable: true, superioChipId: 0xD428, superioRaw: 0x64);

            SensorCatalogEntry? ecTemp  = SensorCatalog.Find("nct6687d.temp.0");
            SensorCatalogEntry? bsTemp  = SensorCatalog.Find("nct6775.temp.0");
            SensorCatalogEntry? bsVolt  = SensorCatalog.Find("nct6775.volt.0");
            Check("catalog has nct6687d.temp.0 + nct6775.temp.0 + nct6775.volt.0",
                  ecTemp != null && bsTemp != null && bsVolt != null);
            if (ecTemp == null || bsTemp == null || bsVolt == null) return failures + 1;

            Check("NCT6683 id lights the EC channels", ecTemp.IsAvailable(nct6683));
            Check("NCT6686 id lights the EC channels", ecTemp.IsAvailable(nct6686));
            Check("EC ids do NOT light nct6775.*", !bsTemp.IsAvailable(nct6683) && !bsTemp.IsAvailable(nct6686));
            Check("NCT6798 id lights nct6775.*", bsTemp.IsAvailable(nct6798));
            Check("NCT6798 id does NOT light the EC channels", !ecTemp.IsAvailable(nct6798));

            SensorReading v = bsVolt.Read(nct6798);                       // raw 0x64 -> 100 * 8 mV = 0.800 V
            Check("nct6775 voltage decode (0x64 -> 0.800 V)", v.Ok && Math.Abs(v.Value - 0.800) < 1e-9);

            var nct6798Temp = new MockSmbusBackend(available: true, superioAvailable: true, superioChipId: 0xD428, superioRaw: 0x8064);
            SensorReading t = bsTemp.Read(nct6798Temp);                   // 0x8064 -> 100 + 0.5
            Check("nct6775 temp decode (0x8064 -> 100.5 C)", t.Ok && Math.Abs(t.Value - 100.5) < 1e-9);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] chipset: threw {ex.Message}");
            failures++;
        }

        return failures;
    }

    /*-----------------------------------------------------------*\
    | Backend/decoder registry integrity (Phase 3 of the           |
    | calibration plan). Asserts:                                  |
    |   * ChannelRegistry.Validate() is clean,                     |
    |   * every registered channel has decoder coverage,           |
    |   * every DriverBackends name the broker table declares      |
    |     exists in the driver's enumeration (mock mirrors the     |
    |     kernel registry names — the name contract),              |
    |   * enumeration Active flags track the detected chip id.     |
    \*-----------------------------------------------------------*/
    private static int SelfTestBackendRegistry()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] registry: {name}");
            if (!ok) failures++;
        }

        try
        {
            string? invalid = ChannelRegistry.Validate();
            Check($"channel registry validates{(invalid == null ? "" : $" ({invalid})")}", invalid == null);

            string? uncovered = DecoderRegistry.FirstUncovered(ChannelRegistry.All);
            Check($"decoder coverage complete{(uncovered == null ? "" : $" (missing: {uncovered})")}", uncovered == null);

            var nctEc = new MockSmbusBackend(available: true, smuAvailable: true,
                                             superioAvailable: true, superioChipId: 0xD592);
            IReadOnlyList<BackendInfo> enumerated = nctEc.EnumerateBackends();
            var enumeratedNames = new HashSet<string>(enumerated.Select(b => b.Name), StringComparer.Ordinal);

            string? unknown = ChannelRegistry.Backends
                .SelectMany(d => d.DriverBackends)
                .FirstOrDefault(n => !enumeratedNames.Contains(n));
            Check($"broker registry names exist in driver enumeration{(unknown == null ? "" : $" (unknown: {unknown})")}",
                  unknown == null);

            Check("NCT6687D id -> 'NCT668x EC' active, 'NCT6775' inactive",
                  enumerated.Single(b => b.Name == "NCT668x EC").Active &&
                  !enumerated.Single(b => b.Name == "NCT6775").Active);

            var nct6775 = new MockSmbusBackend(available: true, superioAvailable: true, superioChipId: 0xD428);
            IReadOnlyList<BackendInfo> enum6775 = nct6775.EnumerateBackends();
            Check("NCT6798 id -> 'NCT6775' active, 'NCT668x EC' inactive",
                  enum6775.Single(b => b.Name == "NCT6775").Active &&
                  !enum6775.Single(b => b.Name == "NCT668x EC").Active);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] registry: threw {ex.Message}");
            failures++;
        }

        return failures;
    }

    /*-----------------------------------------------------------*\
    | Board-aware RGB catalog integrity. Asserts:                  |
    |   * RgbCatalog.Validate() is clean,                          |
    |   * the MSI dev-box profile covers the full zone vocabulary  |
    |     (Dram + Mb12V + MbArgb) — the "same features" parity,    |
    |   * the generic fallback is DRAM-only,                       |
    |   * the registry gates each transport (DRAM on CAP_WRITE; EC |
    |     on CAP_SUPERIO_RGB; HID only when AllowHidRgb) and        |
    |     applies calibration labels by zone id.                   |
    \*-----------------------------------------------------------*/
    private static int SelfTestRgbCatalog()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] rgb: {name}");
            if (!ok) failures++;
        }

        try
        {
            string? invalid = RgbCatalog.Validate();
            Check($"rgb catalog validates{(invalid == null ? "" : $" ({invalid})")}", invalid == null);

            var msi = new BoardIdentity("Micro-Star International Co., Ltd.", "MPG B550I GAMING EDGE MAX WIFI (MS-7C92)");
            RgbBoardProfile msiProfile = RgbCatalog.Resolve(msi);
            Check("MSI B550I profile covers full zone vocabulary (parity)",
                  !msiProfile.IsGeneric && RgbCatalog.MissingKinds(msiProfile).Count == 0);

            RgbBoardProfile generic = RgbCatalog.Resolve(new BoardIdentity("Unknown", "Unknown Board"));
            Check("unknown board -> generic DRAM-only fallback",
                  generic.IsGeneric && generic.Zones.All(z => z.Kind == RgbZoneKind.Dram));

            /* Window assertion (mirrors the kernel brick-guard): a real zone is in-window; a zone
               that targets SPD (0x50) or an out-of-window EC address is refused at the broker. */
            var ram0 = new RgbZone("ram0", "x", RgbZoneKind.Dram, RgbTransport.SmbusEne, 5, Bus: 0, Address: 0x39);
            var spd  = new RgbZone("evil", "x", RgbZoneKind.Dram, RgbTransport.SmbusEne, 1, Bus: 0, Address: 0x50);
            var ecBad = new RgbZone("ecbad", "x", RgbZoneKind.Mb12V, RgbTransport.SuperioEc, 1, EcAddress: 0x0100);
            Check("in-window SMBus zone accepted", RgbCatalog.ZoneAddressFault(ram0) == null);
            Check("out-of-window SMBus zone (SPD 0x50) refused", RgbCatalog.ZoneAddressFault(spd) != null);
            Check("out-of-window EC zone (sensor bank 0x0100) refused", RgbCatalog.ZoneAddressFault(ecBad) != null);

            /* USB-HID product-id pin: a pinned PID selects exactly that device; a pinned-but-absent
               PID refuses (never falls back to another MSI HID); unpinned takes the first candidate. */
            var pids = new ushort[] { 0x7C92, 0x1563 };
            Check("HID pin selects the matching PID", RgbRegistry.SelectHidIndex(pids, 0x1563) == 1);
            Check("HID pin absent -> refuse (no wrong-device fallback)", RgbRegistry.SelectHidIndex(pids, 0x9999) == -1);
            Check("HID unpinned -> first candidate", RgbRegistry.SelectHidIndex(pids, 0) == 0);
            Check("HID unpinned, none present -> refuse", RgbRegistry.SelectHidIndex(Array.Empty<ushort>(), 0) == -1);

            /* DRAM write path only (no EC RGB, no HID): just the two DRAM zones appear. */
            var smbusOnly = new MockSmbusBackend(available: true, superioAvailable: true, superioChipId: 0xD592);
            using (RgbRegistry r = RgbRegistry.Build(smbusOnly, msi, CalibrationStore.Builtin, allowHidRgb: false, _ => { }))
            {
                Check("DRAM-only host -> 2 zones (ram0, ram1)",
                      r.Devices.Count == 2 && r.Find("ram0") != null && r.Find("ram1") != null);
                Check("EC 12V zone inert without CAP_SUPERIO_RGB", r.Find("mb.jrgb0") == null);
                Check("HID ARGB zone absent without AllowHidRgb", r.Find("mb.argb0") == null);
            }

            /* EC RGB advertised (mock) -> the 12V header zone instantiates and a label override applies. */
            var ecOn = new MockSmbusBackend(available: true, superioAvailable: true, superioChipId: 0xD592) { SuperioRgbAvailable = true };
            string defaultCalib = Path.Combine(AppContext.BaseDirectory, "calibration.default.json");
            CalibrationStore calib = CalibrationStore.Load(msi, _ => { }, defaultCalib);
            using (RgbRegistry r = RgbRegistry.Build(ecOn, msi, calib, allowHidRgb: false, _ => { }))
            {
                IRgbController? jrgb = r.Find("mb.jrgb0");
                Check("EC RGB advertised -> mb.jrgb0 present (Mb12V/SuperioEc)",
                      jrgb != null && jrgb.Kind == RgbZoneKind.Mb12V && jrgb.Transport == RgbTransport.SuperioEc);
                Check("calibration relabels zone id", jrgb != null && jrgb.Label == "Case 12V Strip (JRGB)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] rgb: threw {ex.Message}");
            failures++;
        }

        return failures;
    }

    private static async Task<int> SelfTestRateLimitCase(string name, string pipeName)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(
                ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await client.ConnectAsync(3000);

            await BrokerControlServer.WriteFrameAsync(client,
                new { type = "hello", protocol = BrokerProtocol.Version, scopes = new[] { "sensors:read" } }, CancellationToken.None);
            JsonElement ok = await BrokerControlServer.ReadFrameAsync(client, CancellationToken.None);
            string token = ok.GetProperty("token").GetString()!;

            int pong = 0, deny = 0;
            for (int i = 0; i < 8; i++)   // burst is 3, so a fast flood of 8 must be throttled
            {
                await BrokerControlServer.WriteFrameAsync(client, new { token, op = "ping" }, CancellationToken.None);
                JsonElement r = await BrokerControlServer.ReadFrameAsync(client, CancellationToken.None);
                if (r.GetProperty("type").GetString() == "pong") pong++; else deny++;
            }

            bool ok2 = pong > 0 && deny > 0;
            Console.WriteLine($"  [{(ok2 ? "PASS" : "FAIL")}] {name}: pong={pong} deny={deny} (expect both > 0)");
            return ok2 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] {name}: exception {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> SelfTestRejectedCase(string name, string pipeName)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(
                ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await client.ConnectAsync(3000);
            try
            {
                await BrokerControlServer.WriteFrameAsync(client,
                    new { type = "hello", protocol = BrokerProtocol.Version, scopes = new[] { "sensors:read" } }, CancellationToken.None);
                await BrokerControlServer.ReadFrameAsync(client, CancellationToken.None);
                Console.WriteLine($"  [FAIL] {name}: server responded instead of closing");
                return 1;
            }
            catch (EndOfStreamException) { Console.WriteLine($"  [PASS] {name}: connection closed by server"); return 0; }
            catch (IOException)          { Console.WriteLine($"  [PASS] {name}: connection closed by server"); return 0; }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] {name}: exception {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> SelfTestCase(string name, string pipeName, string[] scopes, string op, string expect, string? id = null)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(
                ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await client.ConnectAsync(3000);

            await BrokerControlServer.WriteFrameAsync(client,
                new { type = "hello", protocol = BrokerProtocol.Version, scopes }, CancellationToken.None);
            JsonElement helloResp = await BrokerControlServer.ReadFrameAsync(client, CancellationToken.None);
            string respType = helloResp.GetProperty("type").GetString()!;

            string result;
            if (respType == "ok")
            {
                string token = helloResp.GetProperty("token").GetString()!;
                object request = id != null
                    ? new { token, op, id }
                    : new { token, op };
                await BrokerControlServer.WriteFrameAsync(client, request, CancellationToken.None);
                JsonElement dataResp = await BrokerControlServer.ReadFrameAsync(client, CancellationToken.None);
                result = dataResp.GetProperty("type").GetString()!;
            }
            else
            {
                result = respType;
            }

            bool ok = result == expect;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: got '{result}', expected '{expect}'");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] {name}: exception {ex.Message}");
            return 1;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /*-----------------------------------------------------------*\
    | Strip control characters (CR/LF/etc.) from any message      |
    | before it lands in a log line. Log/audit lines embed         |
    | peer-controlled strings (client image path, Authenticode     |
    | signer subject), and a malicious client signed by a self-    |
    | made cert can put newlines in its subject — without this it   |
    | could forge or split audit-log lines (log injection).        |
    \*-----------------------------------------------------------*/
    private static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        var sb = new StringBuilder(message.Length);
        foreach (char c in message)
            sb.Append(char.IsControl(c) && c != '\t' ? '�' : c);
        return sb.ToString();
    }

    /*-----------------------------------------------------------*\
    | Size-cap rotation. The broker runs as a long-lived          |
    | LocalSystem service; an authorized-but-abusive client can    |
    | otherwise grow audit.log without bound (disk-fill DoS). When  |
    | a log passes the cap, roll it to .1 (replacing the previous   |
    | .1) so each file is bounded and one rollover is kept.         |
    \*-----------------------------------------------------------*/
    private const long MaxLogBytes = 5L * 1024 * 1024;   // 5 MB
    private static void RotateIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxLogBytes)
            {
                string rolled = path + ".1";
                File.Delete(rolled);
                File.Move(path, rolled);
            }
        }
        catch { }
    }

    private static void Log(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Sanitize(message)}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Config.LogFileExpanded) ?? ".");
            RotateIfNeeded(Config.LogFileExpanded);
            File.AppendAllText(Config.LogFileExpanded, line + Environment.NewLine);
        }
        catch { }

        if (Environment.UserInteractive && Console.IsOutputRedirected == false)
        {
            try { Console.WriteLine(line); } catch { }
        }
    }

    /*-----------------------------------------------------------*\
    | Dedicated control-plane audit trail (separate from the      |
    | diagnostic log): connects, auth decisions, every op + its   |
    | result, rate-limit and session-limit rejections.            |
    \*-----------------------------------------------------------*/
    private static readonly object AuditLock = new();
    private static void Audit(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Sanitize(message)}";
        try
        {
            string path = Config.AuditLogFileExpanded;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            lock (AuditLock) { RotateIfNeeded(path); File.AppendAllText(path, line + Environment.NewLine); }
        }
        catch { }
    }
}

internal sealed class BridgeConfig
{
    public string LogFile { get; set; } = "%LOCALAPPDATA%\\BrokerSensorBridge\\bridge.log";

    /*-----------------------------------------------------------*\
    | Control-channel client authorization (see ClientAuthorization). |
    | Off by default for back-compat. Set RequireAuthorizedClient |
    | true, then authorize callers by EITHER:                     |
    |   • AllowedClientPaths   — full image paths (for self-built,|
    |                            unsigned binaries), or            |
    |   • AllowedClientSigners — Authenticode signer SHA-1         |
    |                            thumbprints (the stronger pin).   |
    \*-----------------------------------------------------------*/
    public bool RequireAuthorizedClient { get; set; } = false;
    public string[] AllowedClientPaths { get; set; } = Array.Empty<string>();
    public string[] AllowedClientSigners { get; set; } = Array.Empty<string>();

    /*-----------------------------------------------------------*\
    | Service-grade guardrails (always on, independent of the     |
    | identity gate). Rate-limit per session, bound the session   |
    | table, and audit every control event to a separate log.     |
    \*-----------------------------------------------------------*/
    public double MaxOpsPerSecond { get; set; } = 30.0;
    public double RateBurst { get; set; } = 60.0;
    public int MaxSessions { get; set; } = 32;
    // Per-identity session cap so one client (keyed by image path) can't fill the
    // whole session table and starve other consumers (reconnect-flood DoS).
    public int MaxSessionsPerIdentity { get; set; } = 8;
    public string AuditLogFile { get; set; } = "%LOCALAPPDATA%\\BrokerSensorBridge\\audit.log";

    /*-----------------------------------------------------------*\
    | Opt-in USB-HID RGB (MSI Mystic Light) for addressable        |
    | motherboard headers. OFF by default: unlike the SMBus/EC     |
    | paths it does NOT pass the kernel brick-guard, so the broker's|
    | baked report builder is the only boundary (see SECURITY.md). |
    | Enable via appsettings.json (installer flag) or --allow-hid-rgb.|
    \*-----------------------------------------------------------*/
    public bool AllowHidRgb { get; set; } = false;

    [JsonIgnore]
    public string LogFileExpanded => Environment.ExpandEnvironmentVariables(LogFile);
    [JsonIgnore]
    public string AuditLogFileExpanded => Environment.ExpandEnvironmentVariables(AuditLogFile);

    public static BridgeConfig Load(string[] args, Action<string>? log = null)
    {
        BridgeConfig cfg = new();
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(path))
        {
            try
            {
                cfg = JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? cfg;
            }
            catch (Exception ex)
            {
                /* A config that exists but won't parse is an error state, not a reason to
                   silently fall back to open (audit-only) defaults — that would defeat an
                   operator who set RequireAuthorizedClient=true. Fail CLOSED and log loudly. */
                cfg = new BridgeConfig { RequireAuthorizedClient = true };
                log?.Invoke($"appsettings.json FAILED to parse ({ex.Message}); failing closed (RequireAuthorizedClient=ON).");
            }
        }

        /* CLI override (handy for bring-up): --allow-hid-rgb forces the USB-HID RGB transport on. */
        if (args.Any(a => a.Equals("--allow-hid-rgb", StringComparison.OrdinalIgnoreCase)))
            cfg.AllowHidRgb = true;

        return cfg;
    }
}
