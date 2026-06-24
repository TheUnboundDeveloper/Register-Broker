using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| BrokerControlServer                                                        |
|                                                                            |
|   The hardened, authenticated control channel. A named-pipe server          |
|   speaking a small length-prefixed JSON protocol (v2 — no HMAC):           |
|                                                                            |
|     (connect)         : server validates peer identity/signature; an       |
|                         unauthorized peer is dropped with no reply.         |
|     client -> server : {"type":"hello","protocol":2,"scopes":[...]}        |
|     server -> client : {"type":"ok","token":"<b64>","scopes":[...]}        |
|                        | (connection closed)                                |
|     client -> server : {"token":"<b64>","op":"sensor.list"|"ping"|...}     |
|     server -> client : {"type":"data","op":...,"sensors":[...]}            |
|                        | {"type":"pong"} | {"type":"deny"}                  |
|                                                                            |
|   Authentication is by pipe DACL + peer-process identity + Authenticode     |
|   signer pin (ClientAuthorization); there is no shared secret. Every        |
|   failure path returns a uniform {"type":"deny"} (or a silent close) so an  |
|   unauthorized caller learns nothing. This named pipe is the broker's only  |
|   serving surface — sensors and RGB control both ride it (no TCP/HTTP).      |
\*---------------------------------------------------------------------------*/
internal sealed class BrokerControlServer
{
    /* Control frames are tiny JSON (hello + ops). Cap reads at 64 KB so a client can't make
       the server allocate large buffers; the server-to-client sensor payload is written, not
       read, so it is unaffected. */
    private const int MaxFrameBytes = 64 * 1024;

    /* How long a connected-but-silent client has to send its hello before being dropped.
       Without this an unauthenticated peer can hold a handler + pipe instance forever
       (slowloris); MaxSessions/rate-limit only apply AFTER a successful hello. */
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    private readonly string _pipeName;
    private readonly Action<string> _log;
    private readonly Action<string> _audit;
    private readonly ClientAuthorization _authorization;
    private readonly ISmbusBackend _smbus;
    private readonly BrokerPolicy _policy;
    private readonly string[] _allowedScopes;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly object _sessionGate = new();                       // guards the count-check + insert + per-identity counts
    private readonly ConcurrentDictionary<Task, byte> _handlers = new(); // in-flight client handlers (drained on stop)

    // Persistent per-identity rate limiters: keyed by the stable peer identity so a
    // client cannot reset its token bucket by reconnecting (the limiter outlives the
    // session). Pruned lazily when an identity has no active sessions.
    private readonly ConcurrentDictionary<string, RateLimiter> _limitersByIdentity = new();

    // Global pre-auth connection throttle: bounds how fast NEW connections can force the
    // expensive peer-signature verification (WinVerifyTrust hashes the client image) before
    // any session exists. Generous — legitimate clients connect rarely; this only blunts a
    // same-user connect-flood. Identity-keyed limits can't apply yet (no identity pre-auth).
    private readonly RateLimiter _connectionLimiter;
    // Active session count per identity (guarded by _sessionGate) for the per-identity cap.
    private readonly Dictionary<string, int> _sessionsByIdentity = new();

    // Session lifetime is a SLIDING window: stamped at hello and pushed forward on every
    // authorized op (see HandleRequestAsync), so a continuously-used connection NEVER expires
    // — only one that goes genuinely idle (no op for SessionTtl) ages out. This replaces the
    // old hard 10-minute cap, which silently killed live consumers mid-session: once it fired
    // the broker rejected every op while the pipe stayed open, and a long-lived client kept
    // replaying its dead token forever (only a full restart recovered). internal so the
    // selftest can assert it stayed the long window.
    internal static readonly TimeSpan SessionTtl = TimeSpan.FromHours(24);

    private sealed record Session(HashSet<string> Scopes, string Who, string Identity, RateLimiter Limiter)
    {
        // Mutable so the sliding window can be refreshed in place. The owning connection's
        // handler is the only writer (its ops are sequential); PruneExpiredSessions only reads.
        public DateTime ExpiresUtc { get; set; }
    }

    private readonly bool _allowRgbWrite;
    private readonly RgbRegistry? _rgb;

    public BrokerControlServer(string pipeName, Action<string> log,
                               ClientAuthorization authorization, ISmbusBackend smbus,
                               Action<string>? audit = null, BrokerPolicy? policy = null,
                               bool allowRgbWrite = false, RgbRegistry? rgb = null)
    {
        _pipeName = pipeName;
        _log = log;
        _audit = audit ?? log;             // default: fold audit into the main log
        _authorization = authorization;
        _smbus = smbus;
        _policy = policy ?? BrokerPolicy.Default;
        _allowRgbWrite = allowRgbWrite;
        _rgb = rgb;
        // Allow connection attempts at the per-session op rate (burst), which is far above any
        // legitimate connect cadence yet caps a flood that would otherwise spin WinVerifyTrust.
        _connectionLimiter = new RateLimiter(_policy.MaxOpsPerSecond, _policy.RateBurst);

        /* sensors:read is always offered; smbus:read only when a driver backs it. rgb:write
           is offered ONLY by the dedicated control service (allowRgbWrite) AND when the RGB
           registry actually has a drivable device — so a client can never be granted a
           capability that isn't real, and the sensor broker never offers writes. (The
           registry is transport-agnostic, so this is deliberately not tied to smbus.) */
        var scopes = new List<string> { "sensors:read" };
        if (smbus.Available) scopes.Add("smbus:read");
        if (_allowRgbWrite && _rgb is { Any: true }) scopes.Add("rgb:write");
        _allowedScopes = scopes.ToArray();
    }

    public async Task RunAsync(CancellationToken token)
    {
        _log($@"Control channel listening on \\.\pipe\{_pipeName}");
        PipeSecurity? security = TryBuildPipeSecurity();
        bool servedAny = false;

        // Keepalive refresh loop for devices whose firmware reverts unless periodically re-sent.
        Task keepalive = Task.Run(() => RgbKeepaliveLoopAsync(token), CancellationToken.None);

        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                // Remote clients (SMB \\host\pipe\SensorBroker) are rejected per-connection
                // in HandleClientAsync via PeerIdentity.IsRemote — the managed PipeOptions
                // enum has no RejectRemoteClients flag, so the OS-level reject isn't available.
                server = security is null
                    ? new NamedPipeServerStream(
                        _pipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous)
                    : NamedPipeServerStreamAcl.Create(
                        _pipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                        0, 0, security);
            }
            catch (Exception ex)
            {
                _log("Control pipe create failed: " + ex.Message);
                /* Failing to create the very first instance is fatal (bad name, ACL/privilege,
                   or the pipe is already owned). Surface it so a hosted service reports a
                   failed start instead of silently "running" with no channel. A later failure
                   (after we've served at least once) just ends the loop. */
                if (!servedAny) throw new IOException($"Control pipe '{_pipeName}' could not be created: {ex.Message}", ex);
                break;
            }

            try
            {
                await server.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException) { server.Dispose(); break; }
            catch (Exception ex) { _log("Control pipe wait failed: " + ex.Message); server.Dispose(); continue; }

            servedAny = true;
            Task handler = Task.Run(() => HandleClientAsync(server, token));
            _handlers[handler] = 0;
            _ = handler.ContinueWith(t => _handlers.TryRemove(t, out _), TaskScheduler.Default);
        }

        /* Drain in-flight client handlers before returning so the caller can dispose the
           kernel-driver handle without racing a DeviceIoControl still in flight. Bounded, so a
           wedged handler can't block shutdown forever. */
        Task[] outstanding = _handlers.Keys.ToArray();
        if (outstanding.Length > 0)
            await Task.WhenAny(Task.WhenAll(outstanding), Task.Delay(TimeSpan.FromSeconds(5)));

        await Task.WhenAny(keepalive, Task.Delay(TimeSpan.FromSeconds(2)));
        _log("Control channel stopped.");
    }

    /// <summary>
    /// Periodically re-sends the last color to RGB devices whose firmware reverts to its stored effect
    /// unless refreshed (KeepaliveIntervalMs > 0). Each such device is volatile (no flash write), so a
    /// refresh is wear-free; Refresh() is serialized with rgb.set inside the controller's own lock.
    /// </summary>
    private async Task RgbKeepaliveLoopAsync(CancellationToken token)
    {
        IRgbController[] devices = (_rgb?.Devices ?? Array.Empty<IRgbController>())
            .Where(d => d.KeepaliveIntervalMs > 0).ToArray();
        if (devices.Length == 0) return;

        int tick = Math.Max(50, devices.Min(d => d.KeepaliveIntervalMs) / 2);
        _log($"[rgb] keepalive: {devices.Length} device(s) [{string.Join(", ", devices.Select(d => d.Id))}], tick {tick} ms");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nextDue = new long[devices.Length];   // first tick fires a refresh for each
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(tick, token); }
            catch (OperationCanceledException) { break; }

            long now = sw.ElapsedMilliseconds;
            for (int i = 0; i < devices.Length; i++)
            {
                if (now < nextDue[i]) continue;
                try { devices[i].Refresh(); } catch { /* device unplugged / transport gone — keep looping */ }
                nextDue[i] = now + devices[i].KeepaliveIntervalMs;
            }
        }
    }

    /*-----------------------------------------------------------------------*\
    | Pipe security. Two regimes, keyed on whether we run as LocalSystem (the  |
    | system-service case) or as an ordinary/elevated user (the dev case):     |
    |                                                                          |
    |  • Same-user (dev): grant the owning user full access. The broker and     |
    |    its client run as the same user, so a same-user DACL plus the          |
    |    identity/signer gate is enough.                                        |
    |                                                                          |
    |  • Service (LocalSystem): clients are arbitrary non-admin users in their  |
    |    own sessions, so a same-user DACL would lock everyone out. Grant        |
    |    SYSTEM + Administrators full control and Authenticated Users connect    |
    |    (read/write) rights. No integrity-label SACL is set: a pipe with no     |
    |    explicit label is treated as MEDIUM integrity, which medium-IL clients  |
    |    can already write to, so the DACL is the gate. The real authentication  |
    |    is the peer-process identity + Authenticode signer pin, not the DACL —  |
    |    this regime is meant to pair with RequireAuthorizedClient = true.       |
    |                                                                          |
    | Returns null (default ACL) if anything fails, so the broker still starts. |
    \*-----------------------------------------------------------------------*/
    private PipeSecurity? TryBuildPipeSecurity()
    {
        bool asSystem;
        try
        {
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            asSystem = WindowsIdentity.GetCurrent().User?.Equals(system) == true;
        }
        catch { asSystem = false; }

        if (!asSystem)
        {
            try
            {
                SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
                var ps = new PipeSecurity();
                ps.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
                _log("Control pipe ACL: same-user full access (dev mode).");
                return ps;
            }
            catch (Exception ex)
            {
                _log("Pipe security setup failed, using default ACL: " + ex.Message);
                return null;
            }
        }

        /*-- Service (LocalSystem) regime. DACL only — NO mandatory-label SACL.
               D: SYSTEM full (SY), Administrators full (BA), Authenticated Users connect
                  read+write (AU, GR|GW — enough for a PipeDirection.InOut client open).

             A named pipe created without an explicit integrity label is treated as MEDIUM
             integrity by Windows, so a medium-IL (non-elevated) client can already write to
             it — the DACL above is what actually gates access, backed by the identity/signer
             pin. An explicit label would only be needed to admit LOW-IL (sandboxed) clients,
             which we don't. It is also actively harmful here: applying any SACL at pipe
             creation needs SeSecurityPrivilege *enabled*, which the LocalSystem token does
             not have by default, so a label ACE made CreateNamedPipe fail with
             "a required privilege is not held by the client". --*/
        const string sddl = "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGW;;;AU)";
        try
        {
            var ps = new PipeSecurity();
            ps.SetSecurityDescriptorSddlForm(sddl);
            _log("Control pipe ACL: service mode (SYSTEM/Admins full; AuthenticatedUsers connect; medium-IL default).");
            return ps;
        }
        catch (Exception ex)
        {
            _log("Pipe security setup failed, using default ACL: " + ex.Message);
            return null;
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using (pipe)
        {
            try
            {
                /*-----------------------------------------------------------*\
                | Pre-auth connection throttle: bound the rate of new         |
                | connections before doing any expensive per-peer work        |
                | (signature verification). Generous; a flood is dropped       |
                | with no reply, learning nothing.                            |
                \*-----------------------------------------------------------*/
                if (!_connectionLimiter.TryConsume())
                {
                    _audit("REJECT connect-throttle");
                    return;
                }

                /*-----------------------------------------------------------*\
                | Drop remote (SMB) clients first: this is a local-only       |
                | broker, and remote peers would otherwise be served in       |
                | audit-only mode (they resolve to no local identity).        |
                \*-----------------------------------------------------------*/
                if (PeerIdentity.IsRemote(pipe.SafePipeHandle))
                {
                    _audit("REJECT remote-client");
                    return;
                }

                /*-----------------------------------------------------------*\
                | Peer-identity/signature gate. An unauthorized client is     |
                | dropped here with no reply at all, so it learns nothing.    |
                \*-----------------------------------------------------------*/
                AuthDecision decision = _authorization.Authorize(pipe.SafePipeHandle);
                if (!decision.Allowed)
                {
                    _audit($"REJECT connect ({decision.Who})");
                    return;
                }

                /*-- Client opens with a hello declaring the scopes it wants. Bound the wait so
                      a connected-but-silent peer can't park the handler indefinitely. --*/
                JsonElement hello;
                using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    helloCts.CancelAfter(HelloTimeout);
                    try { hello = await ReadFrameAsync(pipe, helloCts.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        _audit($"REJECT hello-timeout ({decision.Who})");
                        return;
                    }
                }

                /* Drop the connection if the hello is malformed OR resolves to no usable scope
                   (e.g. a client asking only for a scope this broker doesn't offer) — issuing a
                   token for a do-nothing session would diverge from the deny/close contract. */
                if (!TryProcessHello(hello, out HashSet<string> granted) || granted.Count == 0)
                {
                    await WriteFrameAsync(pipe, new { type = "deny" }, token);
                    _audit($"REJECT hello ({decision.Who})");
                    return;
                }

                /*-- Bound the session table so it can't be exhausted. The count-check and the
                      insert must be atomic, or concurrent connections can overshoot the cap. --*/
                PruneExpiredSessions();
                // Share one rate limiter per identity so reconnecting can't reset the bucket.
                RateLimiter limiter = _limitersByIdentity.GetOrAdd(
                    decision.Identity, _ => new RateLimiter(_policy.MaxOpsPerSecond, _policy.RateBurst));
                var session = new Session(granted, decision.Who, decision.Identity, limiter)
                {
                    ExpiresUtc = DateTime.UtcNow.Add(SessionTtl)
                };
                string sessionToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                bool admitted;
                string denyReason = "";
                lock (_sessionGate)
                {
                    _sessionsByIdentity.TryGetValue(decision.Identity, out int perId);
                    if (_sessions.Count >= _policy.MaxSessions)        { admitted = false; denyReason = "session-limit"; }
                    else if (perId >= _policy.MaxSessionsPerIdentity)  { admitted = false; denyReason = "identity-limit"; }
                    else
                    {
                        admitted = true;
                        _sessions[sessionToken] = session;
                        _sessionsByIdentity[decision.Identity] = perId + 1;
                    }
                }
                if (!admitted)
                {
                    await WriteFrameAsync(pipe, new { type = "deny" }, token);
                    _audit($"REJECT {denyReason} ({decision.Who}); active={_sessions.Count}");
                    return;
                }

                await WriteFrameAsync(pipe, new { type = "ok", token = sessionToken, scopes = granted.ToArray() }, token);
                _audit($"AUTH ok via {decision.How} ({decision.Who}) scopes=[{string.Join(",", granted)}]");

                try
                {
                    while (!token.IsCancellationRequested && pipe.IsConnected)
                    {
                        JsonElement req;
                        try { req = await ReadFrameAsync(pipe, token); }
                        catch (EndOfStreamException) { break; }
                        if (!await HandleRequestAsync(pipe, req, token)) break;  // stale session -> close so client reconnects
                    }
                }
                finally
                {
                    /* Tie a session's lifetime to its connection: drop it as soon as the client
                       disconnects, instead of letting it linger to the 10-minute expiry. This
                       keeps the bounded session table from filling with dead sessions — e.g. the
                       launcher's readiness probes, which connect, do one op, and disconnect. */
                    _sessions.TryRemove(sessionToken, out _);
                    lock (_sessionGate)
                    {
                        if (_sessionsByIdentity.TryGetValue(decision.Identity, out int n))
                        {
                            if (n <= 1) _sessionsByIdentity.Remove(decision.Identity);
                            else        _sessionsByIdentity[decision.Identity] = n - 1;
                        }
                    }
                    // NOTE: the limiter is deliberately NOT removed here. If it were, a
                    // reconnect would get a fresh full bucket — exactly the reconnect-reset
                    // bypass we're closing. Stale limiters are pruned by TTL in
                    // PruneExpiredSessions (by then the bucket is fully refilled anyway).
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (IOException) { /* client disconnected */ }
            catch (Exception ex) { _log("Control client error: " + ex.Message); }
        }
    }

    private bool TryProcessHello(JsonElement hello, out HashSet<string> granted)
    {
        granted = new HashSet<string>();

        if (!hello.TryGetProperty("type", out var t) || t.GetString() != "hello") return false;

        if (hello.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sc.EnumerateArray())
            {
                string? v = s.GetString();
                if (v != null) granted.Add(v);
            }
        }
        if (granted.Count == 0) granted.Add("sensors:read");

        granted.IntersectWith(_allowedScopes);            // never grant a scope we can't actually back
        return true;
    }

    private static readonly TimeSpan LimiterIdleTtl = TimeSpan.FromMinutes(2);
    private void PruneExpiredSessions()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var kv in _sessions)
            if (kv.Value.ExpiresUtc < now)
                _sessions.TryRemove(kv.Key, out _);

        // Prune idle per-identity limiters. Safe to drop once idle past the TTL: with no
        // ops for that long the token bucket has fully refilled, so re-creating it later
        // grants nothing a kept limiter wouldn't. Only prune identities with no active
        // session (guard against racing a just-admitted connection).
        foreach (var kv in _limitersByIdentity)
        {
            if (now - kv.Value.LastUseUtc <= LimiterIdleTtl) continue;
            lock (_sessionGate)
            {
                if (!_sessionsByIdentity.ContainsKey(kv.Key))
                    _limitersByIdentity.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>Handle one request frame.</summary>
    /// <returns><c>true</c> to keep the connection open; <c>false</c> to close it. We CLOSE
    /// (rather than reply "deny") on an unknown/expired session token so the client sees a
    /// plain transport drop and its normal reconnect path re-authenticates transparently —
    /// re-hello, fresh session, resend. This keeps the stale-session contract entirely on the
    /// broker: no reconnect rule is imposed on consumers (the old "deny + keep pipe open"
    /// left a long-lived client replaying a dead token forever, recoverable only by restart).</returns>
    private async Task<bool> HandleRequestAsync(NamedPipeServerStream pipe, JsonElement req, CancellationToken token)
    {
        string? tok = req.TryGetProperty("token", out var tk) ? tk.GetString() : null;
        string? op  = req.TryGetProperty("op", out var o) ? o.GetString() : null;
        string? id  = req.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

        if (tok == null || !_sessions.TryGetValue(tok, out var session) || session.ExpiresUtc < DateTime.UtcNow)
        {
            // No reply: drop the connection so the client reconnects (and resends this op on
            // the fresh session). Sending a frame here would let it keep the dead session.
            _audit($"OP close(no-session) op={op}");
            return false;
        }

        /*-- Slide the session's expiry forward on every authorized op: continued use keeps a
              connection alive indefinitely, so a long-running consumer never ages out — only a
              genuinely idle session (no op for SessionTtl) expires and gets pruned. --*/
        session.ExpiresUtc = DateTime.UtcNow.Add(SessionTtl);

        /*-- Rate limit every authorized session, independent of the identity gate. --*/
        if (!session.Limiter.TryConsume())
        {
            await WriteFrameAsync(pipe, new { type = "deny" }, token);
            _audit($"OP rate-limited ({session.Who}) op={op}");
            return true;
        }

        string result;
        switch (op)
        {
            /* Catalog-only sensor access: clients name a logical sensor, never an
               address/register, and cannot enumerate hardware (see SENSOR-MAP.md /
               the sensor-catalog-no-scan rule). The raw smbus.read op was removed from
               the broker path for this reason; raw addressing survives only in the
               admin-only `--smbus-read` dev probe that opens the driver directly. */
            case "sensor.list" when session.Scopes.Contains("sensors:read"):
                result = await HandleSensorListAsync(pipe, token);
                break;

            case "sensor.read" when session.Scopes.Contains("sensors:read"):
                result = await HandleSensorReadAsync(pipe, id, token);
                break;

            /* Batch read of every available catalog sensor in ONE op — so a consumer that
               wants the whole set each poll (e.g. the plugin) costs 1 op/cycle against the
               rate limiter instead of N. Still catalog-only; no addressing. */
            case "sensor.readall" when session.Scopes.Contains("sensors:read"):
                result = await HandleSensorReadAllAsync(pipe, token);
                break;

            /* RGB write — catalog-only, control service only (rgb:write scope). The client
               names a baked device + a color; it never supplies an address. */
            case "rgb.list" when session.Scopes.Contains("rgb:write"):
                result = await HandleRgbListAsync(pipe, token);
                break;

            case "rgb.set" when session.Scopes.Contains("rgb:write"):
                result = await HandleRgbSetAsync(pipe, req, token);
                break;

            case "ping":
                await WriteFrameAsync(pipe, new { type = "pong" }, token);
                result = "pong";
                break;

            default:
                await WriteFrameAsync(pipe, new { type = "deny" }, token);
                result = "deny";
                break;
        }

        _audit($"OP ({session.Who}) op={op}{(id != null ? $" id={id}" : "")} -> {result}");
        return true;
    }

    private async Task<string> HandleSensorListAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        /* 'removable' marks a hot-pluggable source (e.g. the Aquacomputer Quadro over USB-HID):
           the entry legitimately drops out of this list when its controller is unplugged, so a
           consumer can show "not connected" instead of flagging a vanished sensor as an error.
           Omitted from the frame for the common (false) case to keep the list compact. */
        var items = SensorCatalog.Available(_smbus)
            .Select(e => e.Removable
                ? (object)new { id = e.Id, label = e.Label, unit = e.Unit, removable = true }
                : new { id = e.Id, label = e.Label, unit = e.Unit })
            .ToArray();
        await WriteFrameAsync(pipe, new { type = "data", op = "sensor.list", sensors = items }, token);
        return "data";
    }

    private async Task<string> HandleSensorReadAllAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        /* Read every currently-available catalog sensor server-side and return the set in one
           frame. Entries that fail to read this cycle are simply omitted (the consumer keeps
           its last value / marks stale). The N driver reads happen inside the broker — the
           client still spends only one rate-limited op. */
        var items = new List<object>();
        foreach (SensorCatalogEntry e in SensorCatalog.Available(_smbus))
        {
            SensorReading r = e.Read(_smbus);
            if (r.Ok) items.Add(new { id = e.Id, label = e.Label, value = r.Value, unit = r.Unit });
        }
        await WriteFrameAsync(pipe, new { type = "data", op = "sensor.readall", sensors = items }, token);
        return "data";
    }

    private async Task<string> HandleSensorReadAsync(NamedPipeServerStream pipe, string? id, CancellationToken token)
    {
        SensorCatalogEntry? entry = id == null ? null : SensorCatalog.Find(id);

        /* Unknown or unavailable id -> uniform deny. There is no address input and no
           way to probe for "what exists" beyond the published catalog (sensor.list). */
        if (entry == null || !entry.IsAvailable(_smbus))
        {
            await WriteFrameAsync(pipe, new { type = "deny" }, token);
            return "deny";
        }

        SensorReading r = entry.Read(_smbus);
        if (r.Ok)
        {
            await WriteFrameAsync(pipe, new { type = "data", op = "sensor.read", id = entry.Id, value = r.Value, unit = r.Unit }, token);
            return "data";
        }

        await WriteFrameAsync(pipe, new { type = "error", op = "sensor.read", id = entry.Id, status = r.Status }, token);
        return "error";
    }

    private async Task<string> HandleRgbListAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var items = (_rgb?.Devices ?? Array.Empty<IRgbController>())
            .Select(d => new
            {
                id = d.Id,
                label = d.Label,
                leds = d.LedCount,
                kind = d.Kind.ToString().ToLowerInvariant(),        // dram / mb12v / mbargb
                transport = d.Transport.ToString().ToLowerInvariant() // smbusene / superioec / usbhid
            }).ToArray();
        await WriteFrameAsync(pipe, new { type = "data", op = "rgb.list", devices = items }, token);
        return "data";
    }

    private async Task<string> HandleRgbSetAsync(NamedPipeServerStream pipe, JsonElement req, CancellationToken token)
    {
        string? device = req.TryGetProperty("device", out var d) ? d.GetString() : null;
        IRgbController? dev = device == null ? null : _rgb?.Find(device);
        if (dev == null)
        {
            await WriteFrameAsync(pipe, new { type = "deny" }, token);
            return "deny";
        }

        /* Two forms, both catalog-only (the client names a device, never an address):
             • "colors": ["RRGGBB", …]  — per-LED (consumer frame updates), or
             • "color":  "RRGGBB"        — whole device one color (back-compat).               */
        bool ok;
        if (req.TryGetProperty("colors", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var colors = new List<(byte R, byte G, byte B)>(arr.GetArrayLength());
            foreach (var c in arr.EnumerateArray())
            {
                if (c.GetString() is string hex && TryParseColor(hex, out byte cr, out byte cg, out byte cb))
                    colors.Add((cr, cg, cb));
                else
                {
                    await WriteFrameAsync(pipe, new { type = "deny" }, token);   // any bad entry -> deny
                    return "deny";
                }
            }
            ok = colors.Count > 0 && dev.SetLeds(colors);
        }
        else
        {
            string? color = req.TryGetProperty("color", out var c) ? c.GetString() : null;
            if (color == null || !TryParseColor(color, out byte r, out byte g, out byte b))
            {
                await WriteFrameAsync(pipe, new { type = "deny" }, token);
                return "deny";
            }
            ok = dev.SetAll(r, g, b);
        }

        if (ok)
        {
            await WriteFrameAsync(pipe, new { type = "data", op = "rgb.set", device = dev.Id }, token);
            return "data";
        }
        await WriteFrameAsync(pipe, new { type = "error", op = "rgb.set", device = dev.Id }, token);
        return "error";
    }

    private static bool TryParseColor(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return false;
        try
        {
            r = Convert.ToByte(hex.Substring(0, 2), 16);
            g = Convert.ToByte(hex.Substring(2, 2), 16);
            b = Convert.ToByte(hex.Substring(4, 2), 16);
            return true;
        }
        catch { return false; }
    }

    /*-----------------------------------------------------------*\
    | Framing: 4-byte big-endian length prefix + UTF-8 JSON.      |
    | Exposed internally so the in-process self-test client can   |
    | reuse exactly the same wire format.                         |
    \*-----------------------------------------------------------*/
    internal static Task WriteFrameAsync(PipeStream pipe, object obj, CancellationToken token)
        => WriteRawFrameAsync(pipe, JsonSerializer.Serialize(obj), token);

    internal static async Task WriteRawFrameAsync(PipeStream pipe, string json, CancellationToken token)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await pipe.WriteAsync(frame, token);
        await pipe.FlushAsync(token);
    }

    internal static async Task<JsonElement> ReadFrameAsync(PipeStream pipe, CancellationToken token)
    {
        byte[] lenBuf = await ReadExactAsync(pipe, 4, token);
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len < 0 || len > MaxFrameBytes) throw new InvalidDataException("control frame too large");
        byte[] body = await ReadExactAsync(pipe, len, token);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static async Task<byte[]> ReadExactAsync(PipeStream pipe, int count, CancellationToken token)
    {
        byte[] buf = new byte[count];
        int off = 0;
        while (off < count)
        {
            int n = await pipe.ReadAsync(buf.AsMemory(off, count - off), token);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
        return buf;
    }
}
