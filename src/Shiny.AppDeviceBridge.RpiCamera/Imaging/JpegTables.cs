namespace Shiny.AppDeviceBridge.RpiCamera.Imaging;

/// <summary>
/// The fixed tables a baseline JPEG encoder needs, all of them straight out of Annex K of the
/// standard.
/// </summary>
/// <remarks>
/// These are the tables essentially every encoder ships with. Using the standard ones rather
/// than tables optimised per image costs a few percent of file size and buys the guarantee that
/// any decoder anywhere can read the result - which matters more for a photograph crossing a
/// Bluetooth link to an unknown phone than the few percent does.
/// </remarks>
static class JpegTables
{
    /// <summary>Coefficients in one block.</summary>
    public const int BlockSize = 64;

    /// <summary>
    /// Maps a zig-zag position to its index in a naturally ordered block. Coefficients are
    /// stored and transmitted in this order so that the high frequencies - which quantize to
    /// zero far more often - end up together at the end, where a single end-of-block code
    /// covers all of them.
    /// </summary>
    public static ReadOnlySpan<byte> ZigZag =>
    [
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    ];

    /// <summary>Base luminance quantization table, in natural order, for quality 50.</summary>
    public static ReadOnlySpan<ushort> LuminanceQuantization =>
    [
        16, 11, 10, 16,  24,  40,  51,  61,
        12, 12, 14, 19,  26,  58,  60,  55,
        14, 13, 16, 24,  40,  57,  69,  56,
        14, 17, 22, 29,  51,  87,  80,  62,
        18, 22, 37, 56,  68, 109, 103,  77,
        24, 35, 55, 64,  81, 104, 113,  92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103,  99
    ];

    /// <summary>
    /// Base chrominance quantization table, in natural order, for quality 50. Far coarser than
    /// the luminance one, because the eye barely notices.
    /// </summary>
    public static ReadOnlySpan<ushort> ChrominanceQuantization =>
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99
    ];

    /// <summary>
    /// The DCT basis, as <c>0.5 * c(u) * cos((2x+1) u pi / 16)</c>, indexed <c>[u * 8 + x]</c>.
    /// </summary>
    /// <remarks>
    /// Precomputed once. Applying it along rows and then along columns performs the full 2-D
    /// transform including its normalisation, so the encoder itself contains no trigonometry.
    /// </remarks>
    public static readonly float[] Cosines = BuildCosines();

    /// <summary>
    /// Scales a base table for a quality setting, using the scaling every JPEG encoder inherited
    /// from the IJG reference implementation - so a given number here means what it means
    /// everywhere else.
    /// </summary>
    /// <param name="table">A quality-50 base table.</param>
    /// <param name="quality">1-100.</param>
    public static ushort[] ScaleQuantization(ReadOnlySpan<ushort> table, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);

        var scale = quality < 50
            ? 5000 / quality
            : 200 - quality * 2;

        var scaled = new ushort[BlockSize];
        for (var i = 0; i < BlockSize; i++)
        {
            // Never zero: a zero divisor would be a divide by zero on the way in and a lost
            // coefficient on the way out.
            var value = (table[i] * scale + 50) / 100;
            scaled[i] = (ushort)Math.Clamp(value, 1, 255);
        }

        return scaled;
    }

    // Huffman code lengths: entry n is how many symbols use a code of n+1 bits.

    public static ReadOnlySpan<byte> DcLuminanceBits => [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    public static ReadOnlySpan<byte> DcLuminanceValues => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    public static ReadOnlySpan<byte> DcChrominanceBits => [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    public static ReadOnlySpan<byte> DcChrominanceValues => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    public static ReadOnlySpan<byte> AcLuminanceBits => [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];

    public static ReadOnlySpan<byte> AcLuminanceValues =>
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12,
        0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08,
        0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16,
        0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
        0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
        0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
        0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6,
        0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4,
        0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA,
        0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA
    ];

    public static ReadOnlySpan<byte> AcChrominanceBits => [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    public static ReadOnlySpan<byte> AcChrominanceValues =>
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21,
        0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34,
        0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38,
        0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
        0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96,
        0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4,
        0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2,
        0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9,
        0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA
    ];

    public static readonly JpegHuffmanTable DcLuminance = new(DcLuminanceBits, DcLuminanceValues);
    public static readonly JpegHuffmanTable AcLuminance = new(AcLuminanceBits, AcLuminanceValues);
    public static readonly JpegHuffmanTable DcChrominance = new(DcChrominanceBits, DcChrominanceValues);
    public static readonly JpegHuffmanTable AcChrominance = new(AcChrominanceBits, AcChrominanceValues);

    static float[] BuildCosines()
    {
        var table = new float[BlockSize];
        for (var u = 0; u < 8; u++)
        {
            var normalisation = u == 0
                ? 0.5 / Math.Sqrt(2)
                : 0.5;

            for (var x = 0; x < 8; x++)
                table[u * 8 + x] = (float)(normalisation * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        }
        return table;
    }
}


/// <summary>
/// A Huffman table in the form the encoder needs it: the code and its length, looked up by
/// symbol.
/// </summary>
/// <remarks>
/// JPEG stores a table as counts-per-length plus the symbols in order, from which the codes
/// are derivable but not directly usable. This inverts it once at startup.
/// </remarks>
sealed class JpegHuffmanTable
{
    readonly int[] codes = new int[256];
    readonly int[] lengths = new int[256];

    public JpegHuffmanTable(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        var code = 0;
        var index = 0;

        for (var length = 1; length <= 16; length++)
        {
            for (var i = 0; i < bits[length - 1]; i++)
            {
                var symbol = values[index++];
                this.codes[symbol] = code;
                this.lengths[symbol] = length;
                code++;
            }

            // Codes of the next length are one bit longer and continue from here, which is what
            // makes the set prefix-free.
            code <<= 1;
        }
    }

    /// <summary>The code assigned to a symbol.</summary>
    public int Code(int symbol) => this.codes[symbol];

    /// <summary>How many bits of <see cref="Code"/> are significant. Zero for an unused symbol.</summary>
    public int Length(int symbol) => this.lengths[symbol];
}
