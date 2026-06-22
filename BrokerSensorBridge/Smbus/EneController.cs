using System.Text;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| EneController                                                              |
|                                                                            |
|   The ENE/Aura SMBus register protocol used by G.Skill (and other) RGB     |
|   DRAM — a publicly documented hardware protocol, reproduced as register   |
|   facts and cross-checked against open-source reference implementations:   |
|     * set register pointer: write_word_data(cmd 0x00, byteswap(reg))       |
|     * read data byte:        read_byte_data(cmd 0x81)                       |
|     * write data byte:       write_byte_data(cmd 0x01, val)                 |
|     * write data block:      write_block_data(cmd 0x03, bytes)              |
|   The 16-bit register is byte-swapped: ((reg<<8)&0xFF00)|((reg>>8)&0x00FF). |
|                                                                            |
|   This runs in the broker/control service (user mode); the kernel only      |
|   does the bounded, brick-guarded SMBus transactions.                       |
|                                                                            |
|   Frame path (the 2026-06-11 crawl/blink fix, redone):                      |
|     * Each LED's 3 color bytes go out as ONE block write (2 bus             |
|       transactions per LED instead of 6) — atomic per LED, no transient     |
|       wrong-color mixes, and a frame no longer visibly crawls across the    |
|       stick. The ENE-over-SMBus max block payload is 3 bytes.               |
|     * DIRECT mode + APPLY are latched ONCE per controller (enter direct     |
|       mode once; frames are pure color writes): re-latching the mode every  |
|       frame was the visible blink. A failed frame clears the latch so the   |
|       next frame re-latches (device reset / resume recovery).               |
|   Instances are persistent + shared (RgbRegistry), so all bus sequences      |
|   are serialized per controller — two concurrent rgb.set calls must not     |
|   interleave their pointer-write/data-write pairs.                          |
\*---------------------------------------------------------------------------*/
internal sealed class EneController
{
    // ENE register addresses (hardware facts of the ENE/Aura register map).
    public const int ENE_REG_DEVICE_NAME    = 0x1000;   // 16-byte device string
    public const int ENE_REG_COLORS_DIRECT  = 0x8000;   // per-LED direct colors (R,B,G each)
    public const int ENE_REG_DIRECT         = 0x8020;   // direct-mode enable
    public const int ENE_REG_APPLY          = 0x80A0;   // commit
    public const int ENE_APPLY_VAL          = 0x01;

    /// <summary>ENE-over-SMBus max block payload (a protocol fact of the controller).</summary>
    private const int ENE_MAX_BLOCK = 3;

    private readonly ISmbusBackend _drv;
    private readonly int _bus;
    private readonly int _addr;
    private readonly object _io = new();

    private bool _directLatched;      // DIRECT+APPLY written once; cleared on any frame failure
    private bool _blockUnsupported;   // block write rejected (older driver) -> byte fallback

    public EneController(ISmbusBackend drv, int bus, int addr)
    {
        _drv = drv; _bus = bus; _addr = addr;
    }

    private static int Swap(int reg) => ((reg << 8) & 0xFF00) | ((reg >> 8) & 0x00FF);

    /// <summary>Set the ENE register pointer (non-destructive — selects what the next read/write touches).</summary>
    private bool SetPointer(int reg) => _drv.TryWrite(_bus, _addr, 0x00, Swap(reg), word: true, RgbWriteClass.EneDram, out _);

    private bool RegisterWriteLocked(int reg, byte value)
    {
        if (!SetPointer(reg)) return false;
        return _drv.TryWrite(_bus, _addr, 0x01, value, word: false, RgbWriteClass.EneDram, out _);
    }

    /// <summary>
    /// Writes up to ENE_MAX_BLOCK bytes starting at <paramref name="reg"/> as one block
    /// transaction (the ENE block-write command, 0x03). Falls back to per-byte
    /// writes when the kernel driver predates BrokerSmbusWriteBlock (BadRequest once,
    /// remembered) so a mid-deploy bridge/driver mismatch degrades instead of breaking.
    /// </summary>
    private bool RegisterWriteBlockLocked(int reg, ReadOnlySpan<byte> data)
    {
        if (!_blockUnsupported)
        {
            if (!SetPointer(reg)) return false;
            if (_drv.TryWriteBlock(_bus, _addr, 0x03, data, RgbWriteClass.EneDram, out SmbusStatus status)) return true;
            if (status != SmbusStatus.BadRequest) return false;
            _blockUnsupported = true;   // old driver: op unknown — use byte writes from now on
        }
        for (int i = 0; i < data.Length; i++)
            if (!RegisterWriteLocked(reg + i, data[i])) return false;
        return true;
    }

    /// <summary>Latch DIRECT mode + APPLY once per controller; frames stay pure color writes.</summary>
    private bool EnsureDirectLocked()
    {
        if (_directLatched) return true;
        if (!RegisterWriteLocked(ENE_REG_DIRECT, 0x01)) return false;
        if (!RegisterWriteLocked(ENE_REG_APPLY, ENE_APPLY_VAL)) return false;
        _directLatched = true;
        return true;
    }

    public bool RegisterRead(int reg, out byte value)
    {
        lock (_io)
        {
            value = 0;
            if (!SetPointer(reg)) return false;
            SmbusResult r = _drv.Read(_bus, _addr, 0x81, SmbusOp.ReadByte, 1);
            if (!r.Ok || r.Data.Length < 1) return false;
            value = r.Data[0];
            return true;
        }
    }

    public bool RegisterWrite(int reg, byte value)
    {
        lock (_io) return RegisterWriteLocked(reg, value);
    }

    /// <summary>Sets every LED (0..ledCount-1) to one color in DIRECT mode (latched once).</summary>
    public bool SetAllDirect(byte red, byte green, byte blue, int ledCount)
    {
        var colors = new (byte R, byte G, byte B)[ledCount];
        for (int i = 0; i < ledCount; i++) colors[i] = (red, green, blue);
        return SetDirect(colors);
    }

    /// <summary>
    /// Sets each LED to its own color in DIRECT mode: one 3-byte block (R,B,G — the ENE
    /// controller's byte order) per LED at ENE_REG_COLORS_DIRECT + 3*led. DIRECT/APPLY are latched
    /// once per controller, not per frame. <paramref name="colors"/> length is the LED
    /// count to write.
    /// </summary>
    public bool SetDirect(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            if (!EnsureDirectLocked()) { _directLatched = false; return false; }

            Span<byte> rbg = stackalloc byte[ENE_MAX_BLOCK];
            for (int led = 0; led < colors.Count; led++)
            {
                rbg[0] = colors[led].R;   // ENE byte order: R, B, G
                rbg[1] = colors[led].B;
                rbg[2] = colors[led].G;
                if (!RegisterWriteBlockLocked(ENE_REG_COLORS_DIRECT + 3 * led, rbg))
                {
                    _directLatched = false;   // re-latch on the next frame (device may have reset)
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>Reads the 16-byte device-name string (non-destructive). Confirms we're talking to the ENE controller.</summary>
    public string ReadDeviceName()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 16; i++)
        {
            if (!RegisterRead(ENE_REG_DEVICE_NAME + i, out byte b))
                return $"(read failed at offset {i})";
            if (b == 0) break;
            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
        }
        return sb.ToString();
    }
}
