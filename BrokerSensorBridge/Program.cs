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

        if (args.Any(a => a.Equals("--mystic-perled", StringComparison.OrdinalIgnoreCase)))
            return RunMysticPerLedProbe(args);
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
    |   BrokerSensorBridge.exe --hid-scan --vid=1532 > hid.txt      |
    |     (Razer: note the 'iface' of the command interface)        |
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
            Console.WriteLine($"  PID 0x{d.ProductId:X4}  iface={d.InterfaceNumber,2}  featLen={d.FeatureReportByteLength,-4}  usage={d.UsagePage:X2}:{d.Usage:X2}  {d.Path}");
        Console.WriteLine();
        Console.WriteLine("MSI Mystic Light: pin the controller's PID via HidProductId in RgbCatalog.cs (featLen = variant 185/162/112).");
        Console.WriteLine("Razer (VID 1532): the command interface is matched by 'iface' — see RazerHidController.KnownModels (Naga=0, Cynosa=2).");
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
            | GPU sensors (READ-ONLY, USER-MODE, opt-in). When enabled,    |
            | probe the vendor API (AMD ADL) and publish the gpu.* channels|
            | via the provider singleton. Off / no GPU -> the channels     |
            | stay absent (inert-when-absent, like every other backend).   |
            | Read lazily at request time, so wiring it after Configure is |
            | correct. Reduced assurance: no kernel driver, no brick-guard.|
            \*-----------------------------------------------------------*/
            if (Config.AllowGpuSensors)
            {
                GpuSensorProvider.Current = GpuSensorProvider.TryCreate(Log);
                Log(GpuSensorProvider.Current is { IsAvailable: true } gpu
                    ? $"GPU sensors: {gpu.Name} via AMD ADL — read-only, reduced assurance (user-mode, no kernel guard)."
                    : "GPU sensors: enabled (AllowGpuSensors) but no supported GPU detected — gpu.* stay absent.");
            }

            /*-----------------------------------------------------------*\
            | Aquacomputer sensors (READ-ONLY, USER-MODE, REMOVABLE),      |
            | opt-in. An off-board USB-HID controller (Quadro) that can be |
            | unplugged at runtime: the provider runs a hot-plug-aware     |
            | poller so aqua.* appear only while present and never flap.   |
            | Reduced assurance like the GPU/HID paths — no kernel guard.  |
            \*-----------------------------------------------------------*/
            if (Config.AllowAquaSensors)
            {
                AquaSensorProvider.Current = AquaSensorProvider.TryCreate(Log);
                Log(AquaSensorProvider.Current is { IsAvailable: true } aqua
                    ? $"Aqua sensors: {aqua.Name} via USB-HID — read-only, removable, reduced assurance (user-mode, no kernel guard)."
                    : "Aqua sensors: enabled (AllowAquaSensors) but no supported controller detected — aqua.* stay absent.");
            }

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
            (GpuSensorProvider.Current as IDisposable)?.Dispose();
            GpuSensorProvider.Current = null;
            (AquaSensorProvider.Current as IDisposable)?.Dispose();
            AquaSensorProvider.Current = null;
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
        double tctl = SensorDecode.AmdCpuTctlC(raw);
        double tdie = SensorDecode.AmdCpuTctlC(raw, offset);

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
                    (valid ? $"{SensorDecode.AmdCcdTempC(craw):F2} C" : "(not valid / not present)"));
            }
            else Console.WriteLine($"  ccd{c}  ({cst})");
        }

        /* SVI2 voltage telemetry (zenpower). Served only on models with known plane addresses
           (Matisse/Vermeer); compare to HWiNFO "Vcore (SVI2 TFN)" / "SoC Voltage (SVI2 TFN)". */
        Console.WriteLine("SVI voltages (zenpower telemetry):");
        if (backend.SmuVoltagePresent)
        {
            foreach ((uint sensor, string name) in new[] { (9u, "vcore"), (10u, "soc  ") })
            {
                if (backend.TryReadSmuRaw(sensor, out uint vraw, out SmbusStatus vst))
                    Console.WriteLine($"  {name}  raw=0x{vraw:X8}  {SensorDecode.AmdSviVoltageV(vraw):F3} V");
                else
                    Console.WriteLine($"  {name}  ({vst})");
            }
        }
        else Console.WriteLine("  (not available on this CPU model)");
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
        backend.TryWrite(0, 0x50, 0x00, 0x00, word: false, RgbWriteClass.EneDram, out SmbusStatus spd);
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

    /*-----------------------------------------------------------*\
    | Per-LED DIRECT-mode bring-up (MSI Mystic Light report 0x53). |
    | Lights `--count` LEDs starting at flat index `--index` in    |
    | the 240-LED direct array, so the JRAINBOW1 index range can    |
    | be found empirically: sweep --index, watch which physical    |
    | LED lights, then bake HidLedOffset into RgbCatalog.cs.        |
    | Reuses the real controller (enable direct mode + 0x53 frame). |
    | Opt-in dev build only; RGB-only (no fan/voltage reach).       |
    |   --mystic-perled --index=0 --count=1 --color=FF0000          |
    |   --mystic-perled --index=23 --count=60 [--pid=7C92] [--zoneoff=31]|
    \*-----------------------------------------------------------*/
    private static int RunMysticPerLedProbe(string[] args)
    {
        int    hdr1     = ProbeArgInt(args, "--hdr1=", 4);       // 0x53 per-zone selector (JRAINBOW=4, JCORSAIR=5, onboard=6)
        int    hdr2     = ProbeArgInt(args, "--hdr2=", 0);       // sub-selector (JRAINBOW2 = 1)
        int    count    = ProbeArgInt(args, "--count=", 60);
        int    pid      = ProbeArgInt(args, "--pid=", 0x7C92);
        string colorHex = ProbeArgStr(args, "--color=", "FF0000").TrimStart('#');
        bool   noEnable = args.Any(a => a.Equals("--no-enable", StringComparison.OrdinalIgnoreCase));
        if (colorHex.Length != 6 || count < 1)
        {
            Console.WriteLine("usage: --mystic-perled [--hdr1=4] [--hdr2=0] [--count=M] [--color=RRGGBB] [--pid=7C92] [--no-enable]");
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

        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(0x1462, Console.WriteLine);
        Console.WriteLine($"MSI HID interfaces at VID 0x1462: {devs.Count}");
        foreach (HidDevice d0 in devs)
            Console.WriteLine($"  candidate PID 0x{d0.ProductId:X4}  iface={d0.InterfaceNumber}  featLen={d0.FeatureReportByteLength}  usage={d0.UsagePage:X2}:{d0.Usage:X2}");
        HidDevice? dev = devs.FirstOrDefault(d => d.ProductId == pid) ?? devs.FirstOrDefault();
        if (dev is null)
        {
            Console.WriteLine($"No MSI HID device found at VID 0x1462 (looked for PID 0x{pid:X4}). Close OpenRGB/MSI Center and STOP the control service first.");
            foreach (HidDevice d in devs) d.Dispose();
            return 1;
        }

        Console.WriteLine($"Using PID 0x{dev.ProductId:X4} featLen={dev.FeatureReportByteLength} (per-LED 0x53 needs >= 725)");
        if (dev.FeatureReportByteLength < 725)
            Console.WriteLine("  WARNING: featLen < 725 — this device may not expose the 0x53 per-LED report; SetFeature will likely fail.");

        /* Step 1: enable per-LED DIRECT mode via the full 185-byte (0x52) enable packet (the same one
           the controller sends — on_board_led carries the device-wide per-LED master flags). */
        if (!noEnable)
        {
            var enable = new byte[185];
            MysticLightHidController.BuildDirectModeEnable(enable);
            bool setEn = dev.SetFeature(enable);
            System.Threading.Thread.Sleep(15);
            Console.WriteLine($"  enable: full per-LED direct packet  send(0x52@185)={(setEn ? "ok" : "FAIL")}");
        }
        else Console.WriteLine("  enable: SKIPPED (--no-enable)");

        /* Step 2: the 0x53 per-LED frame for the selected zone (hdr1/hdr2). Seed from device
           (best-effort) so untouched LEDs persist, then write `count` LEDs of the color from index 0. */
        int frameLen = 5 + MysticLightHidController.PerLedMaxLeds * 3;   // 725
        var frame = new byte[frameLen];
        frame[0] = 0x53;
        bool seedPl = dev.GetFeature(frame);
        var colors = new (byte, byte, byte)[count];
        for (int i = 0; i < count; i++) colors[i] = (r, g, b);
        MysticLightHidController.BuildPerLedFrame(frame, (byte)hdr1, (byte)hdr2, colors, count);
        bool setPl = dev.SetFeature(frame);
        Console.WriteLine($"  per-LED zone hdr1={hdr1} hdr2={hdr2} count={count} #{colorHex}  "
                        + $"seed={(seedPl ? "ok" : "FAIL")} send(0x53@{frameLen})={(setPl ? "ok" : "FAIL")}");
        Console.WriteLine("Watch the strip. If dark, try --hdr1=4/5/6 and --hdr2=0/1, or --enable-all.");

        foreach (HidDevice d in devs) d.Dispose();
        return setPl ? 0 : 1;
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

        /*-- GPU sensors: the read-only user-mode gpu.* channels are absent without a provider,
              available + decode their value with one, and never light a CPU/board family. --*/
        failures += SelfTestGpuSensors();

        /*-- Aqua sensors: the removable user-mode aqua.* channels are absent without a provider,
              available + decode (temps/flow/fan RPM) with one, gate out a disconnected probe, are
              flagged Removable, and never light a CPU/board family. Also checks the raw decode. --*/
        failures += SelfTestAquaSensors();

        /*-- RGB catalog: board-aware zone profiles validate, the MSI profile resolves the full
              zone vocabulary, the generic fallback is DRAM-only, and label overrides + transport
              gating build the expected device set. --*/
        failures += SelfTestRgbCatalog();

        /*-- Session policy: the lifetime must be the long SLIDING window, not the old hard
              10-minute cap that silently killed live consumers mid-session. This guards against
              a regression back to a short TTL (see the BrokerControlServer sliding-expiry fix). --*/
        {
            bool ttlOk = BrokerControlServer.SessionTtl >= TimeSpan.FromHours(24);
            Console.WriteLine($"  [{(ttlOk ? "PASS" : "FAIL")}] session TTL is the 24h sliding window: got {BrokerControlServer.SessionTtl}");
            failures += ttlOk ? 0 : 1;
        }

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
        failures += await SelfTestCase("sensor.read smu.cpu.vcore -> data", pipeSensor, scopes: new[] { "sensors:read" }, op: "sensor.read", expect: "data", id: "smu.cpu.vcore");
        failures += await SelfTestCase("sensor.read unknown id -> deny",  pipeSensor, scopes: new[] { "sensors:read" }, op: "sensor.read", expect: "deny", id: "no.such.sensor");

        /*-- Stale-session contract: an op carrying an unknown/expired token must DROP the
              connection (so the client's transport-failure reconnect re-authenticates), NOT
              reply with a frame the client would treat as a recoverable op failure. --*/
        failures += await SelfTestStaleSessionCase("stale session token -> connection closed (client must re-hello)", pipeSensor);

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

            /* NCT668x fan PWM duty (READ-ONLY): an EC chip lights nct6687d.pwm.*, the bank-select
               family does not (no pwm channels), and the byte decodes to a percentage. */
            SensorCatalogEntry? ecPwm = SensorCatalog.Find("nct6687d.pwm.0");
            Check("catalog has nct6687d.pwm.0", ecPwm != null);
            if (ecPwm != null)
            {
                Check("NCT6683 id lights the PWM channel", ecPwm.IsAvailable(nct6683));
                Check("NCT6798 id does NOT light the EC PWM channel", !ecPwm.IsAvailable(nct6798));
                var ecFull = new MockSmbusBackend(available: true, superioAvailable: true, superioChipId: 0xD592, superioRaw: 0xFF);
                SensorReading p = ecPwm.Read(ecFull);                     // 0xFF -> 100 %
                Check("nct6687 pwm decode (0xFF -> 100 %)", p.Ok && p.Unit == "%" && Math.Abs(p.Value - 100.0) < 1e-9);
            }
            Check("nct6687 pwm decode (128 -> ~50 %, 0 -> 0 %)",
                  Math.Abs(SensorDecode.NctPwmPercent(0x80) - (128.0 / 255.0 * 100.0)) < 1e-9 &&
                  SensorDecode.NctPwmPercent(0) == 0.0);

            /* AMD SVI2 voltage decode (zenpower plane_to_vcc): V = 1.550 − 0.00625·((raw>>16)&0xFF),
               clamped at 0. code 0x50 (80) -> 1.050 V; code 0xFF -> negative -> clamped to 0. */
            Check("smu SVI voltage decode (0x00500000 -> 1.050 V)",
                  Math.Abs(SensorDecode.AmdSviVoltageV(0x00500000) - 1.050) < 1e-9);
            Check("smu SVI voltage clamp (0x00FF0000 -> 0 V)",
                  SensorDecode.AmdSviVoltageV(0x00FF0000) == 0.0);
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
    | GPU sensors (read-only, user-mode). Asserts:                 |
    |   * the gpu.* channels exist in the catalog,                 |
    |   * with NO provider they are absent (inert-when-absent) and |
    |     the GPU presence never lights a CPU/board family,        |
    |   * with a fixed provider they become available and read the |
    |     supplied value in the right unit, and a metric the GPU   |
    |     does not expose reports "not available" honestly.        |
    | The SMBus backend arg is irrelevant to gpu.* (they key off   |
    | the provider singleton), so a plain mock is used throughout. |
    \*-----------------------------------------------------------*/
    private static int SelfTestGpuSensors()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] gpu: {name}");
            if (!ok) failures++;
        }

        var anyBackend = new MockSmbusBackend(available: true);   // gpu.* ignore the backend
        try
        {
            SensorCatalogEntry? gpuTemp  = SensorCatalog.Find("gpu.temp");
            SensorCatalogEntry? gpuPower = SensorCatalog.Find("gpu.power");
            SensorCatalogEntry? gpuMem   = SensorCatalog.Find("gpu.temp.mem");
            Check("catalog has gpu.temp + gpu.power + gpu.temp.mem",
                  gpuTemp != null && gpuPower != null && gpuMem != null);
            if (gpuTemp == null || gpuPower == null || gpuMem == null) return failures + 1;

            /* No provider -> gpu.* absent; CPU/board sensors unaffected. */
            GpuSensorProvider.Current = null;
            Check("gpu.* absent without a provider", !gpuTemp.IsAvailable(anyBackend) && !gpuPower.IsAvailable(anyBackend));

            /* A fixed provider lights the channels it supplies and decodes their value/unit. */
            GpuSensorProvider.Current = new FixedGpuProvider("Test GPU", new Dictionary<GpuMetric, double>
            {
                [GpuMetric.TempEdge] = 61.0,
                [GpuMetric.PowerW]   = 120.0,
            });
            Check("gpu.temp available with provider", gpuTemp.IsAvailable(anyBackend));
            SensorReading t = gpuTemp.Read(anyBackend);
            Check("gpu.temp reads 61.0 °C", t.Ok && t.Unit == "°C" && Math.Abs(t.Value - 61.0) < 1e-9);
            SensorReading p = gpuPower.Read(anyBackend);
            Check("gpu.power reads 120 W", p.Ok && p.Unit == "W" && Math.Abs(p.Value - 120.0) < 1e-9);

            /* A metric the provider does not expose reports not-available, not a bogus zero. */
            Check("unsupplied gpu metric -> not ok", !gpuMem.Read(anyBackend).Ok);

            /* GPU presence must not light a CPU/board family (cross-source isolation). */
            Check("GPU provider does not light smu.cpu.temp", !SensorCatalog.Find("smu.cpu.temp")!.IsAvailable(anyBackend));
            Check("GPU provider does not light nct6687d.temp.0", !SensorCatalog.Find("nct6687d.temp.0")!.IsAvailable(anyBackend));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] gpu: threw {ex.Message}");
            failures++;
        }
        finally
        {
            GpuSensorProvider.Current = null;   // leave the catalog GPU-free for the later server cases
        }

        return failures;
    }

    /*-----------------------------------------------------------*\
    | Aqua sensors (read-only, user-mode, REMOVABLE). Asserts:     |
    |   * the Quadro status-report decode (ported offsets) yields  |
    |     the right temps/flow/fan RPM, gates a disconnected probe  |
    |     (0x7FFF sentinel), and rejects a too-short report,        |
    |   * the aqua.* channels exist and are flagged Removable,      |
    |   * with NO provider they are absent (inert-when-absent) and  |
    |     never light a CPU/board family,                           |
    |   * with a fixed provider they read the supplied value in the |
    |     right unit, and an unsupplied metric reports not-available.|
    \*-----------------------------------------------------------*/
    private static int SelfTestAquaSensors()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] aqua: {name}");
            if (!ok) failures++;
        }

        var anyBackend = new MockSmbusBackend(available: true);   // aqua.* ignore the backend
        try
        {
            /* Raw decode of a synthetic status report (values from the live dev-box Quadro). */
            byte[] rep = new byte[QuadroProtocol.StatusReportLength];
            rep[0] = QuadroProtocol.StatusReportId;
            void PutBE(int off, ushort v) { rep[off] = (byte)(v >> 8); rep[off + 1] = (byte)v; }
            PutBE(0x34, 0x0BD2);                          // temp0 = 30.26 °C
            PutBE(0x3A, QuadroProtocol.TempSentinel);     // temp3 = disconnected
            PutBE(0x6E, 0x0064);                          // flow = 100 dL/h -> 10.0 L/h
            PutBE(0x70 + 0x08, 0x0E19);                   // fan0 = 3609 RPM
            var dv = new double[QuadroProtocol.SlotCount];
            var dok = new bool[QuadroProtocol.SlotCount];
            Check("Quadro report decodes", QuadroProtocol.TryDecode(rep, rep.Length, dv, dok));
            Check("temp0 -> 30.26 °C", dok[0] && Math.Abs(dv[0] - 30.26) < 1e-9);
            Check("temp3 sentinel -> not ok", !dok[3]);
            Check("flow -> 10.0 L/h", dok[(int)AquaMetric.Flow] && Math.Abs(dv[(int)AquaMetric.Flow] - 10.0) < 1e-9);
            Check("fan0 -> 3609 RPM", dok[(int)AquaMetric.Fan0] && Math.Abs(dv[(int)AquaMetric.Fan0] - 3609) < 1e-9);
            Check("short report rejected", !QuadroProtocol.TryDecode(new byte[16], 16, dv, dok));

            SensorCatalogEntry? aTemp = SensorCatalog.Find("aqua.temp.0");
            SensorCatalogEntry? aFlow = SensorCatalog.Find("aqua.flow.0");
            SensorCatalogEntry? aFan  = SensorCatalog.Find("aqua.fan.0");
            Check("catalog has aqua.temp.0 + flow + fan.0", aTemp != null && aFlow != null && aFan != null);
            if (aTemp == null || aFlow == null || aFan == null) return failures + 1;

            Check("aqua.* flagged removable", aTemp.Removable && aFlow.Removable && aFan.Removable);

            /* No provider -> aqua.* absent; CPU/board sensors unaffected. */
            AquaSensorProvider.Current = null;
            Check("aqua.* absent without a provider", !aTemp.IsAvailable(anyBackend) && !aFan.IsAvailable(anyBackend));

            /* A fixed provider lights the channels it supplies and decodes their value/unit. */
            AquaSensorProvider.Current = new FixedAquaProvider("Test Quadro", new Dictionary<AquaMetric, double>
            {
                [AquaMetric.Temp0] = 30.3,
                [AquaMetric.Flow]  = 10.0,
                [AquaMetric.Fan0]  = 3609,
            });
            Check("aqua.temp.0 available with provider", aTemp.IsAvailable(anyBackend));
            SensorReading t = aTemp.Read(anyBackend);
            Check("aqua.temp.0 reads 30.3 °C", t.Ok && t.Unit == "°C" && Math.Abs(t.Value - 30.3) < 1e-9);
            SensorReading fl = aFlow.Read(anyBackend);
            Check("aqua.flow.0 reads 10.0 L/h", fl.Ok && fl.Unit == "L/h" && Math.Abs(fl.Value - 10.0) < 1e-9);

            /* A metric the provider does not supply reports not-available, not a bogus zero. */
            Check("unsupplied aqua metric (temp.1) -> not ok", !SensorCatalog.Find("aqua.temp.1")!.Read(anyBackend).Ok);

            /* Aqua presence must not light a CPU/board family (cross-source isolation). */
            Check("Aqua provider does not light smu.cpu.temp", !SensorCatalog.Find("smu.cpu.temp")!.IsAvailable(anyBackend));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] aqua: threw {ex.Message}");
            failures++;
        }
        finally
        {
            AquaSensorProvider.Current = null;   // leave the catalog Aqua-free for the later server cases
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

            /* DEVICE-AWARE brick-guard (mirror of the kernel g_RgbWriteProfiles): each device class
               permits ONLY its own window. A Corsair zone (0x58-0x5F, CorsairDram class) is accepted
               in its window; the SAME address under the ENE class, and an ENE address under the
               Corsair class, are BOTH refused — proving per-device isolation, not one shared window. */
            var corsair  = new RgbZone("corsair.dram0", "x", RgbZoneKind.Dram, RgbTransport.Smbus, 10, Bus: 0, Address: 0x58, WriteClass: RgbWriteClass.CorsairDram);
            var corsairHi = new RgbZone("corsair.hi", "x", RgbZoneKind.Dram, RgbTransport.Smbus, 10, Bus: 0, Address: 0x5F, WriteClass: RgbWriteClass.CorsairDram);
            var corsairOut = new RgbZone("corsair.bad", "x", RgbZoneKind.Dram, RgbTransport.Smbus, 10, Bus: 0, Address: 0x39, WriteClass: RgbWriteClass.CorsairDram);
            var eneAt58  = new RgbZone("ene.bad", "x", RgbZoneKind.Dram, RgbTransport.SmbusEne, 5, Bus: 0, Address: 0x58);
            Check("Corsair zone in its window (0x58-0x5F) accepted",
                  RgbCatalog.ZoneAddressFault(corsair) == null && RgbCatalog.ZoneAddressFault(corsairHi) == null);
            Check("Corsair class refuses an ENE address (0x39) — per-device isolation", RgbCatalog.ZoneAddressFault(corsairOut) != null);
            Check("ENE class refuses the Corsair window (0x58) — per-device isolation", RgbCatalog.ZoneAddressFault(eneAt58) != null);

            /* Corsair DRAM protocol math (testable without hardware): CRC-8 (poly 0x07) known-answer,
               the DIRECT packet layout ([ledCount, R,G,B..., CRC]) and the reverse-wiring fact. */
            Check("Corsair CRC-8 known answer (\"123456789\" -> 0xF4)",
                  CorsairDramController.Crc8(System.Text.Encoding.ASCII.GetBytes("123456789")) == 0xF4);
            byte[] cpkt = CorsairDramController.BuildDirectPacket(
                new (byte, byte, byte)[] { (0x11, 0x22, 0x33), (0x44, 0x55, 0x66) }, ledCount: 2, reverse: false);
            Check("Corsair DIRECT packet [ledCount, R,G,B..., CRC]",
                  cpkt.Length == 8 && cpkt[0] == 2 && cpkt[1] == 0x11 && cpkt[4] == 0x44
                  && cpkt[7] == CorsairDramController.Crc8(cpkt.AsSpan(0, 7)));
            byte[] crev = CorsairDramController.BuildDirectPacket(
                new (byte, byte, byte)[] { (0x11, 0x22, 0x33), (0x44, 0x55, 0x66) }, ledCount: 2, reverse: true);
            Check("Corsair reverse wiring flips LED order", crev[1] == 0x44 && crev[4] == 0x11);
            Check("Corsair model table: PID 0x0200 = Dominator Platinum DDR4, 12 LEDs reversed",
                  CorsairDramController.ResolveModel(0x0200) is { LedCount: 12, Reverse: true }
                  && CorsairDramController.SupportsDirect(4) && !CorsairDramController.SupportsDirect(3));

            /* Each new DRAM family's device-aware window (mirror of g_RgbWriteProfiles): in-window
               accepted, and the family's class refuses an address outside its own window. */
            RgbZone Z(RgbWriteClass cls, int a) => new("z", "x", RgbZoneKind.Dram, RgbTransport.Smbus, 8, Bus: 0, Address: a, WriteClass: cls);
            Check("Crucial window (0x20-0x27, 0x39-0x3C) accepted; 0x30 refused",
                  RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.CrucialDram, 0x23)) == null
                  && RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.CrucialDram, 0x3C)) == null
                  && RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.CrucialDram, 0x30)) != null);
            Check("Fury window (0x58-0x67) accepted; 0x57 (SPD edge) refused",
                  RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.FuryDram, 0x67)) == null
                  && RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.FuryDram, 0x57)) != null);
            Check("Viper window (0x77) accepted; 0x76 refused",
                  RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.ViperDram, 0x77)) == null
                  && RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.ViperDram, 0x76)) != null);
            Check("Xtreem window (0x70-0x78, 0x39-0x3D) accepted; 0x79 refused",
                  RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.XtreemDram, 0x78)) == null
                  && RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.XtreemDram, 0x3D)) == null
                  && RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.XtreemDram, 0x79)) != null);
            Check("Corsair Vengeance refuses the JC42 alias (0x18) — only 0x58-0x5F permitted",
                  RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.CorsairVenDram, 0x18)) != null
                  && RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.CorsairVenDram, 0x58)) == null);

            /* T-Force Xtreem folded-strip remap (logical -> physical): 0,14,1,13,... */
            Check("T-Force folded offset (0->0, 1->14, 2->1, 3->13)",
                  TForceXtreemController.FoldedOffset(0, 15) == 0 && TForceXtreemController.FoldedOffset(1, 15) == 14
                  && TForceXtreemController.FoldedOffset(2, 15) == 1 && TForceXtreemController.FoldedOffset(3, 15) == 13);
            Check("DRAM fixed LED counts (Crucial 8, Xtreem 15, Corsair Vengeance 1)",
                  CrucialDramController.LedCount == 8 && TForceXtreemController.LedCount == 15
                  && CorsairVengeanceController.LedCount == 1);

            /* Motherboard SMBus RGB device-aware windows (single-address each). */
            Check("ASRock window (0x6A) accepted; 0x6B refused",
                  RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.AsrockMb, 0x6A)) == null
                  && RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.AsrockMb, 0x6B)) != null);
            Check("EVGA window (0x28) accepted; SPD 0x50 refused under EVGA class",
                  RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.EvgaMb, 0x28)) == null
                  && RgbCatalog.ZoneAddressFault(Z(RgbWriteClass.EvgaMb, 0x50)) != null);

            /* AMD Wraith Prism USB-HID direct packets: enable (0x41/0x80/0x03), apply (0x51/0x28, [5]=0xE0),
               and a 0xC0 direct packet with {id,R,G,B} entries at offset 5 (R,G,B order, 17 LEDs total). */
            byte[] en = WraithPrismController.BuildEnableDirect();
            byte[] ap = WraithPrismController.BuildApply();
            Check("Wraith Prism enable/apply packets (0x41/0x80/0x03; 0x51/0x28 [5]=0xE0)",
                  en.Length == 65 && en[1] == 0x41 && en[2] == 0x80 && en[3] == 0x03
                  && ap[1] == 0x51 && ap[2] == 0x28 && ap[5] == 0xE0);
            byte[] dp = WraithPrismController.BuildDirectPacket(
                new (byte, byte, byte, byte)[] { (0x00, 0x11, 0x22, 0x33), (0x01, 0x44, 0x55, 0x66) });
            Check("Wraith Prism direct packet [0xC0,0x01,size,0x00, id,R,G,B...]",
                  dp[1] == 0xC0 && dp[2] == 0x01 && dp[3] == 2
                  && dp[5] == 0x00 && dp[6] == 0x11 && dp[7] == 0x22 && dp[8] == 0x33
                  && dp[9] == 0x01 && dp[10] == 0x44 && dp[11] == 0x55 && dp[12] == 0x66);
            Check("Wraith Prism LED map = 17 (Logo, Fan, 15 ring); ring id order starts 0x08",
                  WraithPrismController.LedCount == 17 && WraithPrismController.LedIds[2] == 0x08);

            /* Logitech G203 mouse DIRECT (report 0x11, 0x12/0x10, 3 LED selectors 0x01/0x02/0x03 R,G,B). */
            byte[] g203 = LogitechG203LController.BuildDirect(new (byte, byte, byte)[] { (0x11, 0x22, 0x33), (0x44, 0x55, 0x66), (0x77, 0x88, 0x99) });
            Check("Logitech G203 direct packet (0x11/0xFF/0x12/0x10, sel+RGB, end 0xFF@16)",
                  g203[0] == 0x11 && g203[2] == 0x12 && g203[3] == 0x10
                  && g203[4] == 0x01 && g203[5] == 0x11 && g203[8] == 0x02 && g203[9] == 0x44 && g203[16] == 0xFF);
            /* Logitech keyboard PER-KEY: key-table sizes (G810/G910 117, G Pro 94) + the report-0x12
               direct frame ([0x12,0xFF,directB2,0x3D,0,zone,0,count], then {idx,R,G,B}). */
            Check("Logitech key tables (G810 117, G910 117, G Pro 94)",
                  LogitechGKeyboardController.BuildKeyTable(LogitechGKeyboardController.LayoutG810).Length == 117
                  && LogitechGKeyboardController.BuildKeyTable(LogitechGKeyboardController.LayoutG910).Length == 117
                  && LogitechGKeyboardController.BuildKeyTable(LogitechGKeyboardController.LayoutGPro).Length == 94);
            byte[] gfr = LogitechGKeyboardController.BuildDirectFrame(0x0C, 0x01,
                new (byte, byte, byte, byte)[] { (0x04, 0x11, 0x22, 0x33), (0x05, 0x44, 0x55, 0x66) });
            Check("Logitech direct frame (0x12/0xFF/0x0C/0x3D, zone, count, idx+RGB@8)",
                  gfr[0] == 0x12 && gfr[2] == 0x0C && gfr[3] == 0x3D && gfr[5] == 0x01 && gfr[7] == 2
                  && gfr[8] == 0x04 && gfr[9] == 0x11 && gfr[12] == 0x05 && gfr[13] == 0x44);

            /* SteelSeries Rival 3 (cmd 0x05, 8-byte, zoneVal+RGB+brightness) and Aerox (cmd 0x21,
               bitmask 1<<zone, RGB at 3+zone*3). */
            byte[] r3 = SteelSeriesRival3Controller.BuildZone(2, 0x11, 0x22, 0x33, 0x64);
            Check("SteelSeries Rival 3 zone packet (05/00/zoneVal/RGB/bright)",
                  r3.Length == 8 && r3[1] == 0x05 && r3[3] == 0x02 && r3[4] == 0x11 && r3[5] == 0x22 && r3[6] == 0x33 && r3[7] == 0x64);
            byte[] ax = SteelSeriesAeroxController.BuildZone(2, 0xAA, 0xBB, 0xCC);
            Check("SteelSeries Aerox zone packet (0x21, mask 1<<2, RGB at 3+2*3)",
                  ax.Length == 65 && ax[1] == 0x21 && ax[2] == 0x04 && ax[9] == 0xAA && ax[10] == 0xBB && ax[11] == 0xCC);

            /* Corsair iCUE V2: CTRL2 triplet buffer ([0x12,0x00, RGB...]), CTRL1 planar buffer
               (all R, all G, all B), and the BLK_W1 first-packet framing (write_cmd@1, 16-bit length
               @4..5, data@8). 2 LEDs of (R,G,B)=(11,22,33),(44,55,66). */
            var ccol = new (byte, byte, byte)[] { (0x11, 0x22, 0x33), (0x44, 0x55, 0x66) };
            byte[] trip = CorsairV2Controller.BuildBufferTriplet(ccol, 2);
            Check("Corsair V2 CTRL2 triplet buffer ([0x12,0x00,R,G,B...], len count*3+2)",
                  trip.Length == 8 && trip[0] == 0x12 && trip[1] == 0x00
                  && trip[2] == 0x11 && trip[3] == 0x22 && trip[4] == 0x33 && trip[5] == 0x44);
            byte[] plan = CorsairV2Controller.BuildBufferPlanar(ccol, 2);
            Check("Corsair V2 CTRL1 planar buffer ([R0,R1, G0,G1, B0,B1], len count*3)",
                  plan.Length == 6 && plan[0] == 0x11 && plan[1] == 0x44 && plan[2] == 0x22 && plan[3] == 0x55 && plan[4] == 0x33 && plan[5] == 0x66);
            byte[] blk = CorsairV2Controller.BuildBlockFirst(0x08, trip, out int consumed);
            Check("Corsair V2 BLK_W1 framing (wc@1, cmd 0x06, len@4..5, data@8)",
                  blk.Length == 65 && blk[1] == 0x08 && blk[2] == 0x06 && blk[4] == 8 && blk[5] == 0
                  && blk[8] == 0x12 && consumed == 8);

            /* SteelSeries Apex 3 8-zone (cmd 0x21, bitmask 0xFF, 8 RGB triplets at 3) and T-zone
               (cmd 0x0B color, RGB triplets at 3; brightness cmd 0x0A at [3]). */
            byte[] e8 = SteelSeriesApex3Controller.BuildEightZone(new (byte, byte, byte)[] { (0x11, 0x22, 0x33), (0x44, 0x55, 0x66) });
            Check("SteelSeries Apex3 8-zone (00/21/FF, RGB triplets at 3)",
                  e8.Length == 65 && e8[1] == 0x21 && e8[2] == 0xFF && e8[3] == 0x11 && e8[4] == 0x22 && e8[5] == 0x33 && e8[6] == 0x44);
            byte[] tz = SteelSeriesApex3Controller.BuildTZoneColor(new (byte, byte, byte)[] { (0xAA, 0xBB, 0xCC) }, 10);
            byte[] tb = SteelSeriesApex3Controller.BuildTZoneBrightness(0x64);
            Check("SteelSeries Apex3 T-zone (color 00/0B RGB@3; brightness 00/0A @3)",
                  tz.Length == 33 && tz[1] == 0x0B && tz[3] == 0xAA && tz[4] == 0xBB && tz[5] == 0xCC
                  && tb[1] == 0x0A && tb[3] == 0x64);

            /* SteelSeries OldApex 5-zone RGBA ([00,07,00, R,G,B,A...]) and Sensei 2-zone (cmd 0x5B,
               static flag @0x14, count @0x1C, RGB duplicated @0x1D & 0x20). */
            byte[] oa = SteelSeriesOldApexController.BuildColor(new (byte, byte, byte)[] { (0x11, 0x22, 0x33) });
            Check("SteelSeries OldApex (00/07/00, R,G,B,Alpha=FF)",
                  oa.Length == 33 && oa[1] == 0x07 && oa[3] == 0x11 && oa[4] == 0x22 && oa[5] == 0x33 && oa[6] == 0xFF);
            byte[] sn = SteelSeriesSenseiController.BuildZone(1, 0xAA, 0xBB, 0xCC);
            Check("SteelSeries Sensei (5B, zone@3, flag@14, RGB@1D & dup@20)",
                  sn.Length == 66 && sn[1] == 0x5B && sn[3] == 0x01 && sn[0x14] == 0x01 && sn[0x1C] == 0x01
                  && sn[0x1D] == 0xAA && sn[0x20] == 0xAA && sn[0x22] == 0xCC);

            /* NZXT Lift (0x00 prefix; header 0x43/0xAE; LEDs at 26 stride 4, remapped slot order 2,1,0,...). */
            byte[] lift = NzxtLiftController.BuildColor(new (byte, byte, byte)[] { (0x10, 0x20, 0x30), (0x40, 0x50, 0x60), (0x70, 0x80, 0x90), (1, 2, 3), (4, 5, 6), (7, 8, 9) });
            Check("NZXT Lift (00 prefix, 0x43/0xAE header, marker@25, slot0=colors[2]@26)",
                  lift.Length == 65 && lift[1] == 0x43 && lift[2] == 0xAE && lift[25] == 0x06
                  && lift[26] == 0x70 && lift[27] == 0x80 && lift[28] == 0x90);

            /* Roccat Kone Aimo (feature 0x0D/0x2E, 11×4 bytes) and Kone Pro (direct 0x0D/0x0B, 2×3). */
            byte[] aimo = RoccatKoneAimoController.BuildColor(new (byte, byte, byte)[] { (0x11, 0x22, 0x33) });
            Check("Roccat Kone Aimo (0x0D/0x2E, R,G,B,pad@2)",
                  aimo.Length == 46 && aimo[0] == 0x0D && aimo[1] == 0x2E && aimo[2] == 0x11 && aimo[3] == 0x22 && aimo[4] == 0x33);
            byte[] kpro = RoccatKoneProController.BuildColor(new (byte, byte, byte)[] { (0xAA, 0xBB, 0xCC), (0xDD, 0xEE, 0xFF) });
            Check("Roccat Kone Pro direct (0x0D/0x0B, 2×RGB@2) + control enable 0x0E",
                  kpro.Length == 11 && kpro[0] == 0x0D && kpro[1] == 0x0B && kpro[2] == 0xAA && kpro[5] == 0xDD
                  && Roccat.BuildControl(0x01) is [0x0E, 0x06, 0x01, 0x01, 0x00, 0xFF]);

            /* Redragon mouse: register-write (0xF3, addr LE @2..3, len@4, data@8) + apply (0xF1). */
            byte[] rw = RedragonMouseController.BuildWrite(0x0449, new byte[] { 0x11, 0x22, 0x33 });
            byte[] rap = RedragonMouseController.BuildApply();
            Check("Redragon write (02/F3, addr 49/04, len 3, RGB@8) + apply (02/F1)",
                  rw.Length == 16 && rw[0] == 0x02 && rw[1] == 0xF3 && rw[2] == 0x49 && rw[3] == 0x04 && rw[4] == 0x03
                  && rw[8] == 0x11 && rw[9] == 0x22 && rw[10] == 0x33 && rap[1] == 0xF1 && rap[2] == 0x02);
            /* Cooler Master MP750 static (00, 01, 04, R,G,B, speed). */
            byte[] mp = CoolerMasterMp750Controller.BuildStatic(0xAA, 0xBB, 0xCC);
            Check("Cooler Master MP750 static (00/01/04, RGB@3, speed@6)",
                  mp.Length == 65 && mp[1] == 0x01 && mp[2] == 0x04 && mp[3] == 0xAA && mp[4] == 0xBB && mp[5] == 0xCC);

            /* HyperX mice: FPS Pro (07/0A, RGB@2, A0@8), Raid (2 LEDs), Haste (setup 04/F2 + color 81),
               Surge (planar strip R@8/G@28/B@48, logo@6C). */
            byte[] hfps = HyperXPulsefireFpsProController.BuildColor(0x11, 0x22, 0x33);
            Check("HyperX FPS Pro (07/0A, RGB@2, 0xA0@8)",
                  hfps.Length == 264 && hfps[0] == 0x07 && hfps[1] == 0x0A && hfps[2] == 0x11 && hfps[3] == 0x22 && hfps[4] == 0x33 && hfps[8] == 0xA0);
            byte[] hhs = HyperXPulsefireHasteController.BuildSetup();
            byte[] hhc = HyperXPulsefireHasteController.BuildColor(0xAA, 0xBB, 0xCC);
            Check("HyperX Haste (setup 04/F2/02@8; color 81, RGB@2, 02@8)",
                  hhs[1] == 0x04 && hhs[2] == 0xF2 && hhs[8] == 0x02 && hhc[1] == 0x81 && hhc[2] == 0xAA && hhc[8] == 0x02);
            byte[] hsg = HyperXPulsefireSurgeController.BuildColor(new (byte, byte, byte)[] { (0x10, 0x20, 0x30) });
            Check("HyperX Surge planar (14/A0; strip0 R@8 G@28 B@48)",
                  hsg[1] == 0x14 && hsg[3] == 0xA0 && hsg[0x08] == 0x10 && hsg[0x28] == 0x20 && hsg[0x48] == 0x30);

            /* HyperX Alloy FPS: channel-scatter (first key at 0x08). */
            byte[] hch = HyperXAlloyFpsController.BuildChannel(0x01, new (byte, byte, byte)[] { (0x77, 0x88, 0x99) });
            Check("HyperX Alloy FPS channel R (07/16/01/A0; value at KeyOffset[0]=0x08)",
                  hch[0] == 0x07 && hch[1] == 0x16 && hch[2] == 0x01 && hch[3] == 0xA0 && hch[0x08] == 0x77);

            /* HyperX Origins-family: 4-byte color groups [0x81,R,G,B], blanks inserted at skip indices. */
            byte[] hinit = HyperXOriginsController.BuildInit(0x09);
            Check("HyperX Origins init (04/F2/09@9)", hinit[1] == 0x04 && hinit[2] == 0xF2 && hinit[9] == 0x09);
            var hpk = HyperXOriginsController.BuildColorPackets(
                new (byte, byte, byte)[] { (0xA1, 0xA2, 0xA3), (0xB1, 0xB2, 0xB3), (0xC1, 0xC2, 0xC3) }, new[] { 1 }, 1);
            Check("HyperX Origins groups + skip insert (g0=A, g1=blank, g2=B)",
                  hpk[0][1] == 0x81 && hpk[0][2] == 0xA1 && hpk[0][5] == 0x81 && hpk[0][6] == 0x00
                  && hpk[0][9] == 0x81 && hpk[0][10] == 0xB1);

            /* HyperX 0x44 keyboards (Eve 1800 / Origins2 65): init 44/01/04; color 44/02/seq, triplets@4. */
            byte[] h44i = HyperX44KeyboardController.BuildInit();
            byte[] h44c = HyperX44KeyboardController.BuildColorPacket(0, new (byte, byte, byte)[] { (0x11, 0x22, 0x33) }, 0, 1);
            Check("HyperX 0x44 keyboard (init 44/01/04; color 44/02/seq, RGB@4)",
                  h44i[0] == 0x44 && h44i[1] == 0x01 && h44i[2] == 0x04 && h44c[0] == 0x44 && h44c[1] == 0x02 && h44c[4] == 0x11 && h44c[6] == 0x33);

            /* Keepalive wiring: a normal device reports 0; Corsair K55 reports its ~30 s interval. */
            Check("keepalive default is 0 (devices hold their color)",
                  CorsairV2Controller.KnownModels.First(m => m.Id == "corsair.darkcorese").KeepaliveMs == 0
                  && CorsairV2Controller.KnownModels.First(m => m.Id == "corsair.k55pro").KeepaliveMs == 30000);

            /* SteelSeries Rival 100 (cmd 0x05, zone byte 0x00) and Rival 300 (cmd 0x08, zone+1). */
            byte[] r100 = SteelSeriesRivalLegacyController.BuildColor(0x05, 0x00, 0x11, 0x22, 0x33);
            byte[] r300 = SteelSeriesRivalLegacyController.BuildColor(0x08, 0x02, 0xAA, 0xBB, 0xCC);
            Check("SteelSeries Rival 100/300 color (cmd/zone/RGB)",
                  r100.Length == 10 && r100[1] == 0x05 && r100[2] == 0x00 && r100[3] == 0x11
                  && r300[1] == 0x08 && r300[2] == 0x02 && r300[3] == 0xAA && r300[5] == 0xCC
                  && SteelSeriesRivalLegacyController.BuildModeDirect(0x00)[1] == 0x07);

            /* Cooler Master mice: init (41/80) + DIRECT (seed 51/A8) with family-specific zone offsets.
               MM530 = 3 LEDs (wheel@11, buttons@5, logo@8); MM7xx = 2 LEDs (wheel@5, logo@8). */
            var cmMm530 = CoolerMasterMouseController.KnownModels.First(m => m.Id == "cm.mm530");
            byte[] cmd = CoolerMasterMouseController.BuildDirect(
                new (byte, byte, byte)[] { (0x10, 0x11, 0x12), (0x20, 0x21, 0x22), (0x30, 0x31, 0x32) }, cmMm530.ZoneOffsets);
            Check("CM mouse init (41/80) + MM530 direct (51/A8; wheel@11, buttons@5, logo@8)",
                  CoolerMasterMouseController.BuildInit()[1] == 0x41 && cmd[1] == 0x51 && cmd[2] == 0xA8
                  && cmd[11] == 0x10 && cmd[5] == 0x20 && cmd[8] == 0x30);

            /* ASUS ROG Ally: init "ASUS Tech.Inc." + DIRECT feature (5A/D1/08/0C, 4×RGB@4). */
            byte[] allyInit = AsusRogAllyController.BuildInit2();
            byte[] allyDir = AsusRogAllyController.BuildDirect(new (byte, byte, byte)[] { (0x11, 0x22, 0x33), (0x44, 0x55, 0x66), (0x77, 0x88, 0x99), (0xAA, 0xBB, 0xCC) });
            Check("ROG Ally init (5D/41 + 'ASUS') + direct (5A/D1/08/0C, RGB@4)",
                  allyInit[0] == 0x5D && allyInit[1] == 0x41 && allyInit[2] == (byte)'A' && allyInit[5] == (byte)'S'
                  && allyDir[0] == 0x5A && allyDir[1] == 0xD1 && allyDir[2] == 0x08 && allyDir[3] == 0x0C
                  && allyDir[4] == 0x11 && allyDir[7] == 0x44 && allyDir[13] == 0xAA);

            /* Lian Li Uni Hub SL V2: START (E0/10/60, (ch<<4)|fans), COLOR (E0, 0x30+ch, R,B,G order),
               COMMIT (E0, 0x10+ch, static 0x01), + the R+B+G>460 power limiter. */
            byte[] llStart = LianLiUniHubSlV2Controller.BuildStart(2, 1);
            byte[] llColor = LianLiUniHubSlV2Controller.BuildColor(2, new (byte, byte, byte)[] { (0x10, 0x20, 0x30) }, 0, 1);
            Check("Lian Li SL V2 START/COLOR (E0/10/60, ch<<4|fans; E0/0x32, R,B,G@2)",
                  llStart[0] == 0xE0 && llStart[2] == 0x60 && llStart[3] == 0x21 && llColor[1] == 0x32
                  && llColor[2] == 0x10 && llColor[3] == 0x30 && llColor[4] == 0x20);   // R, B(=0x30), G(=0x20)
            var lim = LianLiUniHubSlV2Controller.Limit(200, 200, 200);   // sum 600 > 460 -> scaled
            Check("Lian Li power limiter (R+B+G>460 scaled down)", lim.R < 200 && lim.R == 200 * 460 / 600);

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

            /* MSI Mystic Light packet layout (the JRAINBOW double-brightness/flicker fix): an MbArgb
               (addressable) zone must write the 11-byte RainbowZoneData — the 10 ZoneData fields PLUS
               the cycle_or_led_num LED-count byte at +10; a non-addressable zone writes ZoneData only
               and must leave +10 untouched. Static color = effect 0x01, brightness flags 0x7C,
               colorFlags 0x80 (fixed). zoneOffset 31 = j_rainbow_1; the rainbow byte returns the count. */
            byte[] argb = new byte[185];
            int wrote = MysticLightHidController.BuildZonePacket(argb, 31, RgbZoneKind.MbArgb, 0x11, 0x22, 0x33, 60);
            Check("Mystic Light MbArgb -> ZoneData fields (effect/RGB/brightness/colorFlags)",
                  argb[31] == 0x01 && argb[32] == 0x11 && argb[33] == 0x22 && argb[34] == 0x33
                  && argb[35] == 0x7C && argb[39] == 0x80);
            Check("Mystic Light MbArgb -> cycle_or_led_num LED count at +10",
                  wrote == 60 && argb[41] == 60);

            byte[] solid = new byte[185];
            int wrote2 = MysticLightHidController.BuildZonePacket(solid, 1, RgbZoneKind.Mb12V, 0x11, 0x22, 0x33, 60);
            Check("Mystic Light non-rainbow zone -> ZoneData only (no +10 LED-count byte)",
                  wrote2 == 0 && solid[1] == 0x01 && solid[1 + 10] == 0x00);

            /* The cycle_or_led_num byte is clamped into the protocol's valid range (1..200), so a
               malformed/huge LedCount can never write an out-of-range count. */
            byte[] clampLo = new byte[185]; MysticLightHidController.BuildZonePacket(clampLo, 31, RgbZoneKind.MbArgb, 1, 1, 1, 0);
            byte[] clampHi = new byte[185]; MysticLightHidController.BuildZonePacket(clampHi, 31, RgbZoneKind.MbArgb, 1, 1, 1, 9999);
            Check("Mystic Light rainbow LED count clamped to 1..200", clampLo[41] == 1 && clampHi[41] == 200);

            /* Per-LED DIRECT frame (report 0x53): fixed header [0x53,0x25,0x06,0x00,0x00], then literal
               RGB triplets at ledOffset within the 240-LED array. This is the path that fixes the
               brightness fold on addressable headers (linear per-LED RGB, no firmware sync engine). */
            int frameLen = 5 + MysticLightHidController.PerLedMaxLeds * 3;       // 725
            byte[] pl = new byte[frameLen];
            MysticLightHidController.BuildPerLedFrame(pl, hdr1: 4, hdr2: 0,
                new (byte, byte, byte)[] { (0xAA, 0xBB, 0xCC), (0x11, 0x22, 0x33) }, 2);
            Check("per-LED frame length = 725 (5 header + 240*3)", frameLen == 725);
            Check("per-LED JRAINBOW1 header (0x53 / 0x25 / hdr1=4 / hdr2=0)",
                  pl[0] == 0x53 && pl[1] == 0x25 && pl[2] == 0x04 && pl[3] == 0x00);
            Check("per-LED LED0 at frame index 0 -> bytes 5..7", pl[5] == 0xAA && pl[6] == 0xBB && pl[7] == 0xCC);
            Check("per-LED LED1 at frame index 1 -> bytes 8..10", pl[8] == 0x11 && pl[9] == 0x22 && pl[10] == 0x33);
            Check("per-LED leaves LEDs past the count untouched", pl[11] == 0x00);

            byte[] pl2 = new byte[frameLen];
            MysticLightHidController.BuildPerLedFrame(pl2, hdr1: 4, hdr2: 1,
                new (byte, byte, byte)[] { (1, 2, 3) }, 1);
            Check("per-LED JRAINBOW2 selector (hdr1=4 / hdr2=1)", pl2[2] == 0x04 && pl2[3] == 0x01);

            /* The catalog refuses a per-LED zone whose LedCount overruns the 240-LED frame. */
            var argbBad = new RgbZone("argbbad", "x", RgbZoneKind.MbArgb, RgbTransport.UsbHid, LedCount: 999, HidPerLedHdr1: 4);
            Check("per-LED zone overrunning the 240-LED frame refused", RgbCatalog.ZoneAddressFault(argbBad) != null);

            /* Razer extended-matrix packet math (the brittle part of the USB-HID port): a 1-LED
               custom frame with RGB AA BB CC must carry the right header/args and CRC = XOR[3..88]. */
            byte[] frame = RazerHidController.BuildCustomFrameRow(0, 1, new byte[] { 0xAA, 0xBB, 0xCC });
            Check("Razer custom frame header (class 0x0F id 0x03, size 0x08)",
                  frame.Length == 91 && frame[2] == 0x3F && frame[6] == 0x08 && frame[7] == 0x0F && frame[8] == 0x03);
            Check("Razer custom frame args (row/start/stop + RGB)",
                  frame[11] == 0x00 && frame[12] == 0x00 && frame[13] == 0x00
                  && frame[14] == 0xAA && frame[15] == 0xBB && frame[16] == 0xCC);
            Check("Razer custom frame CRC = XOR[3..88]", frame[89] == 0xD9);

            byte[] apply = RazerHidController.BuildApplyCustom();
            Check("Razer apply-custom (class 0x0F id 0x02 size 0x0C, effect 0x08, CRC)",
                  apply[6] == 0x0C && apply[7] == 0x0F && apply[8] == 0x02 && apply[11] == 0x08 && apply[89] == 0x09);

            Check("Razer known-model geometry (Naga 3 LEDs, Cynosa 132 LEDs)",
                  RazerHidController.KnownModels.Any(m => m.Id == "razer.naga"   && m.Rows * m.Cols == 3) &&
                  RazerHidController.KnownModels.Any(m => m.Id == "razer.cynosa" && m.Rows * m.Cols == 132));

            /* The Razer command interface is matched by USB interface number, parsed from the
               Windows HID device path (&mi_NN); a non-composite path yields -1. */
            Check("HID device-path interface parse (&mi_02 -> 2, none -> -1)",
                  HidDevice.ParseInterfaceNumber(@"\\?\hid#vid_1532&pid_022a&mi_02#7&xyz#{g}") == 2 &&
                  HidDevice.ParseInterfaceNumber(@"\\?\hid#vid_1532&pid_0067#abc") == -1);
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

    /*-----------------------------------------------------------*\
    | Authenticate normally, then send an op with a bogus token.  |
    | The broker must close the connection (transport drop) so a  |
    | real client reconnects, rather than reply with a frame.     |
    \*-----------------------------------------------------------*/
    private static async Task<int> SelfTestStaleSessionCase(string name, string pipeName)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(
                ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await client.ConnectAsync(3000);

            await BrokerControlServer.WriteFrameAsync(client,
                new { type = "hello", protocol = BrokerProtocol.Version, scopes = new[] { "sensors:read" } }, CancellationToken.None);
            JsonElement helloResp = await BrokerControlServer.ReadFrameAsync(client, CancellationToken.None);
            if (helloResp.GetProperty("type").GetString() != "ok")
            {
                Console.WriteLine($"  [FAIL] {name}: hello was not accepted");
                return 1;
            }

            /* Valid connection, invalid session token -> broker must drop the connection. */
            await BrokerControlServer.WriteFrameAsync(client,
                new { token = "not-a-real-session-token", op = "sensor.readall" }, CancellationToken.None);
            try
            {
                await BrokerControlServer.ReadFrameAsync(client, CancellationToken.None);
                Console.WriteLine($"  [FAIL] {name}: server replied instead of closing the connection");
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
    | USB-HID RGB (MSI Mystic Light / Razer / peripherals) for     |
    | addressable headers and devices. ON by default: most hosts   |
    | with RGB control want it, so the common case shouldn't need a |
    | flag. Unlike the SMBus/EC paths it does NOT pass the kernel   |
    | brick-guard, so the broker's baked report builder is the only |
    | boundary (see SECURITY.md). Set false in appsettings.json to  |
    | opt out; --allow-hid-rgb forces it on regardless.            |
    \*-----------------------------------------------------------*/
    public bool AllowHidRgb { get; set; } = true;

    /*-----------------------------------------------------------*\
    | GPU sensors (read-only) via the vendor user-mode API         |
    | (AMD ADL today). OFF by default: a discrete GPU's thermals   |
    | are not an SMBus device, so this loads a vendor library in   |
    | the broker process — like AllowHidRgb it does NOT pass the   |
    | kernel brick-guard (there is no driver involved) and is      |
    | reduced assurance, so it is opt-in. READ-ONLY: no GPU write  |
    | path exists. Set true in appsettings.json (server-side) or   |
    | --allow-gpu-sensors to force it on. See SECURITY.md.         |
    \*-----------------------------------------------------------*/
    public bool AllowGpuSensors { get; set; } = false;

    /*-----------------------------------------------------------*\
    | Aquacomputer sensors (read-only) via USB-HID (Quadro today). |
    | OFF by default, same reduced-assurance posture as GPU: an    |
    | off-board controller streamed in user-mode, no kernel        |
    | brick-guard, no write path. REMOVABLE — the controller may   |
    | be unplugged at runtime; aqua.* are flagged removable and the |
    | provider is hot-plug aware. Set true in appsettings.json      |
    | (server-side) or --allow-aqua-sensors to force it on.        |
    \*-----------------------------------------------------------*/
    public bool AllowAquaSensors { get; set; } = false;

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

        /* CLI override: --allow-gpu-sensors forces the read-only GPU sensor source on. */
        if (args.Any(a => a.Equals("--allow-gpu-sensors", StringComparison.OrdinalIgnoreCase)))
            cfg.AllowGpuSensors = true;

        /* CLI override: --allow-aqua-sensors forces the read-only Aquacomputer (USB-HID) source on. */
        if (args.Any(a => a.Equals("--allow-aqua-sensors", StringComparison.OrdinalIgnoreCase)))
            cfg.AllowAquaSensors = true;

        return cfg;
    }
}
