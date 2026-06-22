namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| CorsairDramController                                                       |
|                                                                            |
|   The Corsair Vengeance RGB Pro / RT DRAM SMBus protocol, reproduced as     |
|   register FACTS (addresses + sequence + CRC) and re-expressed in our own    |
|   code — NOT copied from any project's source. Corsair DRAM RGB lives at     |
|   SMBus 0x58-0x5F, ABOVE the SPD region (0x50-0x57) the kernel refuses, so   |
|   driving it never touches SPD. The kernel brick-guard permits this address  |
|   window ONLY under the BrokerRgbClassCorsairDram device class (Smbus.c),    |
|   so a Corsair write can never reach an ENE/GPU address and vice-versa.      |
|                                                                            |
|   Protocol facts (cross-checked against the public Corsair DRAM register     |
|   map; single primary source — flagged for HW validation, see TESTING.md):   |
|     * Direct mode requires firmware protocol version >= 4 (read from the     |
|       device-info buffer; older sticks expose only effect mode).             |
|     * DIRECT frame = block write to reg 0x31 of:                             |
|         [ledCount, R0,G0,B0, R1,G1,B1, ..., CRC8(all preceding bytes)]       |
|       split across reg 0x31 (first 32 bytes) + reg 0x32 (remainder) when it   |
|       exceeds one SMBus block. CRC8 = poly 0x07, init 0x00 (CRC-8/SMBus-ish).|
|     * Device info: select buffer (write 0x61=0, 0x21=0), then read 32 bytes  |
|       from reg 0x40; VID@[0..1], PID@[2..3], protocol@[28].                  |
|                                                                            |
|   Runs in the control service (user mode); the kernel only does the bounded, |
|   brick-guarded SMBus block/byte transactions.                              |
\*---------------------------------------------------------------------------*/
internal sealed class CorsairDramController
{
    /// <summary>Corsair DRAM SMBus vendor id (in the device-info buffer); identity gate.</summary>
    public const int CorsairDramVid = 0x1B1C;

    /// <summary>Lowest firmware protocol version that supports DIRECT (per-LED literal) mode.</summary>
    private const int MinDirectProtocol = 4;

    // Corsair DRAM register facts.
    private const int REG_SET_BINARY_DATA = 0x20;
    private const int REG_BINARY_START    = 0x21;   // start a binary transfer (write 0x00)
    private const int REG_COLOR_BLOCK_1   = 0x31;   // direct color buffer, block 1
    private const int REG_COLOR_BLOCK_2   = 0x32;   // direct color buffer, block 2 (data > 32 bytes)
    private const int REG_GET_BINARY_DATA = 0x40;   // read byte from the active buffer (auto-increment)
    private const int REG_GET_DEVICE_INFO = 0x61;   // select the device-info buffer (write 0x00)

    /// <summary>One block write carries at most MAX_BLOCK bytes; the kernel enforces the same bound.</summary>
    private const int MaxBlock = 32;

    private readonly ISmbusBackend _drv;
    private readonly int _bus;
    private readonly int _addr;
    private readonly int _ledCount;
    private readonly bool _reverse;
    private readonly object _io = new();

    public CorsairDramController(ISmbusBackend drv, int bus, int addr, int ledCount, bool reverse)
    {
        _drv = drv; _bus = bus; _addr = addr; _ledCount = ledCount; _reverse = reverse;
    }

    public int LedCount => _ledCount;

    /// <summary>
    /// CRC-8 (polynomial 0x07, init 0x00, no reflection) over <paramref name="data"/>. This is the
    /// checksum the Corsair DRAM firmware computes over a binary buffer. Pure/testable (selftest gate).
    /// </summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (byte)(((crc & 0x80) != 0) ? (crc << 1) ^ 0x07 : (crc << 1));
        }
        return crc;
    }

    /// <summary>
    /// Builds the DIRECT-mode color packet for <paramref name="colors"/> (clamped/padded to the
    /// device LED count): [ledCount, R,G,B per LED..., CRC8]. Pure so the selftest can assert the
    /// layout and checksum without hardware. <paramref name="reverse"/> mirrors LED order for the
    /// sticks whose physical wiring runs the opposite way (a per-model fact).
    /// </summary>
    public static byte[] BuildDirectPacket(IReadOnlyList<(byte R, byte G, byte B)> colors, int ledCount, bool reverse)
    {
        int size = ledCount * 3 + 2;
        var packet = new byte[size];
        packet[0] = (byte)ledCount;
        for (int led = 0; led < ledCount; led++)
        {
            int ci = reverse ? (ledCount - 1 - led) : led;
            (byte R, byte G, byte B) c = ci < colors.Count ? colors[ci] : ((byte)0, (byte)0, (byte)0);
            int off = led * 3 + 1;
            packet[off + 0] = c.R;
            packet[off + 1] = c.G;
            packet[off + 2] = c.B;
        }
        packet[size - 1] = Crc8(packet.AsSpan(0, size - 1));
        return packet;
    }

    /// <summary>Sets every LED to one color in DIRECT mode.</summary>
    public bool SetAll(byte r, byte g, byte b)
    {
        var colors = new (byte, byte, byte)[_ledCount];
        for (int i = 0; i < _ledCount; i++) colors[i] = (r, g, b);
        return SetColors(colors);
    }

    /// <summary>
    /// Writes a DIRECT-mode frame: builds the color packet and block-writes it to reg 0x31 (and 0x32
    /// for the overflow past 32 bytes), all under the CorsairDram brick-guard class. Returns false on
    /// any transport error.
    /// </summary>
    public bool SetColors(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        byte[] packet = BuildDirectPacket(colors, _ledCount, _reverse);
        lock (_io)
        {
            int first = Math.Min(MaxBlock, packet.Length);
            if (!_drv.TryWriteBlock(_bus, _addr, REG_COLOR_BLOCK_1, packet.AsSpan(0, first), RgbWriteClass.CorsairDram, out _))
                return false;
            if (packet.Length > MaxBlock)
            {
                if (!_drv.TryWriteBlock(_bus, _addr, REG_COLOR_BLOCK_2, packet.AsSpan(MaxBlock), RgbWriteClass.CorsairDram, out _))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Non-destructively identifies a Corsair DRAM controller at <paramref name="addr"/>: selects the
    /// device-info buffer and reads VID/PID/protocol. Returns true only when the VID matches Corsair.
    /// The selecting writes go to the Corsair RGB address under the CorsairDram class (never SPD).
    /// </summary>
    public static bool TryIdentify(ISmbusBackend drv, int bus, int addr, out ushort pid, out byte protocol)
    {
        pid = 0; protocol = 0;

        if (!drv.TryWrite(bus, addr, REG_GET_DEVICE_INFO, 0x00, word: false, RgbWriteClass.CorsairDram, out _)) return false;
        if (!drv.TryWrite(bus, addr, REG_BINARY_START,    0x00, word: false, RgbWriteClass.CorsairDram, out _)) return false;

        var info = new byte[32];
        for (int i = 0; i < info.Length; i++)
        {
            SmbusResult r = drv.Read(bus, addr, REG_GET_BINARY_DATA, SmbusOp.ReadByte, 1);
            if (!r.Ok || r.Data.Length < 1) return false;
            info[i] = r.Data[0];
        }

        int vid = (info[1] << 8) | info[0];
        pid = (ushort)((info[3] << 8) | info[2]);
        protocol = info[28];
        return vid == CorsairDramVid;
    }

    /// <summary>True when this controller's firmware supports DIRECT (per-LED) mode.</summary>
    public static bool SupportsDirect(byte protocol) => protocol >= MinDirectProtocol;

    /*-- Corsair DRAM model table (PID -> friendly name, LED count, reverse). Hardware FACTS: each
         model's LED count and wiring direction, keyed by the PID reported in the device-info buffer.
         Used to label and size a detected stick. --*/
    public readonly record struct Model(string Name, int LedCount, bool Reverse);

    private static readonly Dictionary<ushort, Model> Models = new()
    {
        [0x0100] = new("Corsair Vengeance RGB Pro DDR4", 10, false),
        [0x0101] = new("Corsair Vengeance RGB Pro DDR4", 10, false),
        [0x0200] = new("Corsair Dominator Platinum RGB DDR4", 12, true),
        [0x0201] = new("Corsair Dominator Platinum RGB DDR4", 12, true),
        [0x0300] = new("Corsair Vengeance RGB Pro SL DDR4", 10, false),
        [0x0301] = new("Corsair Vengeance RGB Pro SL DDR4", 10, false),
        [0x0400] = new("Corsair Vengeance RGB RS DDR4", 6, false),
        [0x0401] = new("Corsair Vengeance RGB RS DDR4", 6, false),
        [0x0600] = new("Corsair Dominator Platinum RGB DDR5", 12, true),
        [0x0601] = new("Corsair Dominator Platinum RGB DDR5", 12, true),
        [0x0700] = new("Corsair Vengeance RGB DDR5", 10, false),
        [0x0701] = new("Corsair Vengeance RGB DDR5", 10, false),
        [0x0800] = new("Corsair Dominator Titanium RGB DDR5", 12, true),
        [0x0801] = new("Corsair Dominator Titanium RGB DDR5", 12, true),
        [0x0810] = new("Corsair Dominator Titanium RGB DDR5", 12, true),
        [0x0811] = new("Corsair Dominator Titanium RGB DDR5", 12, true),
        [0x0900] = new("Corsair Vengeance RGB DDR5", 10, false),
        [0x0901] = new("Corsair Vengeance RGB DDR5", 10, false),
        [0x0910] = new("Corsair Vengeance RGB DDR5", 10, false),
        [0x0911] = new("Corsair Vengeance RGB DDR5", 10, false),
        [0x0A00] = new("Corsair Vengeance Shugo Series DDR5", 10, false),
        [0x0A01] = new("Corsair Vengeance Shugo Series DDR5", 10, false),
        [0x0A10] = new("Corsair Vengeance Shugo Series DDR5", 10, false),
        [0x0A11] = new("Corsair Vengeance Shugo Series DDR5", 10, false),
        [0x0B00] = new("Corsair Vengeance RGB RS DDR5", 6, false),
        [0x0B01] = new("Corsair Vengeance RGB RS DDR5", 6, false),
    };

    /// <summary>Looks up a model by PID; returns a generic 8-LED fallback for an unknown Corsair PID.</summary>
    public static Model ResolveModel(ushort pid) =>
        Models.TryGetValue(pid, out Model m) ? m : new Model($"Corsair DRAM (PID 0x{pid:X4})", 8, false);
}

/*---------------------------------------------------------------------------*\
| CorsairDramRgbController — IRgbController wrapper over the Corsair protocol. |
\*---------------------------------------------------------------------------*/
internal sealed class CorsairDramRgbController : IRgbController
{
    private readonly CorsairDramController _dev;

    public string Id { get; }
    public string Label { get; }
    public int LedCount => _dev.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Dram;
    public RgbTransport Transport => RgbTransport.Smbus;

    public CorsairDramRgbController(ISmbusBackend backend, string id, string label, int bus, int addr, int ledCount, bool reverse)
    {
        _dev = new CorsairDramController(backend, bus, addr, ledCount, reverse);
        Id = id;
        Label = label;
    }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors) => _dev.SetColors(colors);
}
