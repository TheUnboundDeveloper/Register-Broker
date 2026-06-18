namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| RazerHidController                                                         |
|                                                                            |
|   IRgbController over Razer Chroma peripherals (keyboards / mice) on the    |
|   USB-HID "extended matrix" protocol. Ported as a PUBLIC PROTOCOL —         |
|   documented wire facts from the OpenRazer Linux kernel driver, treated as  |
|   register/command FACTS and re-implemented clean-room in C# (not copied    |
|   code; see THIRD-PARTY-NOTICES.md). Razer VID 0x1532.                       |
|                                                                            |
|   WIRE FACTS (razer_report, 91 bytes incl. the leading HID report id):       |
|     [0]  report_id      (0x00)                                             |
|     [1]  status         (0x00)                                             |
|     [2]  transaction_id (0x3F)                                             |
|     [3..4] remaining    (0x0000)                                           |
|     [5]  protocol_type  (0x00)                                             |
|     [6]  data_size                                                         |
|     [7]  command_class                                                     |
|     [8]  command_id                                                        |
|     [9..88] arguments[80]                                                  |
|     [89] crc  = XOR of bytes [3..88]                                       |
|     [90] reserved       (0x00)                                            |
|   Commands used (extended matrix only — RGB, nothing else):                 |
|     * custom frame : class 0x0F id 0x03, data_size = 5 + n*3,               |
|                      args[2]=row, args[3]=startCol, args[4]=stopCol,        |
|                      args[5..]=RGB triples.                                 |
|     * apply custom : class 0x0F id 0x02, data_size 0x0C, args[2]=0x08.      |
|   A frame update writes one custom-frame report per matrix row, then the    |
|   apply-custom report to commit. RGB only — device mode / macros / etc. are |
|   deliberately never touched (minimal surface, like the narrow driver).     |
|                                                                            |
|   USER-MODE, reduced assurance (no kernel brick-guard); opt-in (AllowHidRgb)|
|   and board-independent (matched by VID/PID, not the DMI board profile).    |
|   HW-UNVALIDATED: protocol ported from facts; validate on the bench (see    |
|   docs/RGB-BOARD-BRINGUP.md) before relying on it.                          |
\*---------------------------------------------------------------------------*/
internal sealed class RazerHidController : IRgbController
{
    /// <summary>Razer USB vendor id.</summary>
    internal const ushort RazerVendorId = 0x1532;

    private const int  ReportLen      = 91;
    private const byte TransactionId  = 0x3F;
    private const int  ArgsBase       = 9;     // arguments[0] lives at byte 9

    /// <summary>A known Razer device. The 90-byte command collection is identified by the same
    /// tuple OpenRGB's detector uses — USB interface number + HID usage page + usage (the command
    /// collection is usage 0x01:0x02 with a 91-byte feature report); a device exposes several
    /// collections on one interface, so the usage is needed to disambiguate. Plus matrix geometry
    /// (rows x cols, row-major LEDs).</summary>
    internal sealed record Model(ushort Pid, int Interface, ushort UsagePage, ushort Usage,
                                 string Id, string Label, RgbZoneKind Kind, int Rows, int Cols);

    /// <summary>Devices on the dev box, using the extended-matrix protocol. Command interface +
    /// usage verified by --hid-scan: Naga Trinity = iface 0 / usage 01:02 (3 zones: scroll/logo/
    /// numpad as a 1x3 matrix); Cynosa Chroma = iface 2 / usage 01:02 (6x22 keys).</summary>
    internal static readonly IReadOnlyList<Model> KnownModels = new[]
    {
        new Model(0x0067, 0, 0x01, 0x02, "razer.naga",   "Razer Naga Trinity",  RgbZoneKind.Mouse,    1,  3),
        new Model(0x022A, 2, 0x01, 0x02, "razer.cynosa", "Razer Cynosa Chroma", RgbZoneKind.Keyboard, 6, 22),
    };

    private readonly HidDevice _hid;
    private readonly Model     _model;
    private readonly object    _io = new();

    public string Id => _model.Id;
    public string Label => _model.Label;
    public int LedCount => _model.Rows * _model.Cols;
    public RgbZoneKind Kind => _model.Kind;
    public RgbTransport Transport => RgbTransport.UsbHidRazer;

    public RazerHidController(HidDevice hid, Model model)
    {
        _hid = hid;
        _model = model;
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var frame = new (byte R, byte G, byte B)[LedCount];
        for (int i = 0; i < frame.Length; i++) frame[i] = (r, g, b);
        return SetLeds(frame);
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        if (colors.Count == 0) return false;

        lock (_io)
        {
            bool ok = true;

            /* One custom-frame report per matrix row (LEDs are row-major). */
            byte[] rowRgb = new byte[_model.Cols * 3];
            for (int row = 0; row < _model.Rows; row++)
            {
                for (int col = 0; col < _model.Cols; col++)
                {
                    int idx = row * _model.Cols + col;
                    (byte R, byte G, byte B) c = idx < colors.Count ? colors[idx] : (default, default, default);
                    rowRgb[col * 3 + 0] = c.R;
                    rowRgb[col * 3 + 1] = c.G;
                    rowRgb[col * 3 + 2] = c.B;
                }

                ok &= _hid.SetFeature(BuildCustomFrameRow(row, _model.Cols, rowRgb));
            }

            /* Commit: switch the device to display the custom frame. */
            ok &= _hid.SetFeature(BuildApplyCustom());
            return ok;
        }
    }

    /*-----------------------------------------------------------------------*\
    | Packet builders — internal + pure so --selftest can assert them without  |
    | a device (CRC and field layout are the brittle part of the port).        |
    \*-----------------------------------------------------------------------*/

    /// <summary>Allocate a zeroed razer_report with the fixed header + command fields filled.</summary>
    internal static byte[] CreateReport(byte commandClass, byte commandId, byte dataSize)
    {
        var b = new byte[ReportLen];
        b[0] = 0x00;            // report_id
        b[1] = 0x00;            // status
        b[2] = TransactionId;   // transaction_id
        // [3..4] remaining_packets = 0, [5] protocol_type = 0
        b[6] = dataSize;
        b[7] = commandClass;
        b[8] = commandId;
        return b;
    }

    /// <summary>CRC = XOR of bytes [3..88]; stored at byte [89]. Returns the same buffer.</summary>
    internal static byte[] Finalize(byte[] report)
    {
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= report[i];
        report[89] = crc;
        return report;
    }

    /// <summary>Extended-matrix custom frame for one row: cols 0..(ledsInRow-1), RGB triples.</summary>
    internal static byte[] BuildCustomFrameRow(int row, int ledsInRow, byte[] rowRgb)
    {
        byte stopCol  = (byte)(ledsInRow - 1);
        byte dataSize = (byte)(5 + ledsInRow * 3);
        byte[] b = CreateReport(0x0F, 0x03, dataSize);
        b[ArgsBase + 2] = (byte)row;     // arguments[2] = row index
        b[ArgsBase + 3] = 0x00;          // arguments[3] = start column
        b[ArgsBase + 4] = stopCol;       // arguments[4] = stop column (inclusive)
        Array.Copy(rowRgb, 0, b, ArgsBase + 5, ledsInRow * 3);
        return Finalize(b);
    }

    /// <summary>Apply-custom-mode (extended matrix): commit/display the custom frame.</summary>
    internal static byte[] BuildApplyCustom()
    {
        byte[] b = CreateReport(0x0F, 0x02, 0x0C);
        b[ArgsBase + 0] = 0x00;
        b[ArgsBase + 1] = 0x00;
        b[ArgsBase + 2] = 0x08;          // custom effect
        return Finalize(b);
    }
}
