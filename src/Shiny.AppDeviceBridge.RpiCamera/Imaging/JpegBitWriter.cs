namespace Shiny.AppDeviceBridge.RpiCamera.Imaging;

/// <summary>
/// Writes the entropy-coded segment of a JPEG: variable-length codes packed into bytes, with
/// the byte stuffing the format requires.
/// </summary>
/// <remarks>
/// The stuffing is the only subtle part. A JPEG marker is <c>0xFF</c> followed by a non-zero
/// byte, and entropy-coded data is free to produce <c>0xFF</c> by chance - so every literal
/// <c>0xFF</c> in the scan is followed by a <c>0x00</c> that decoders discard. Forgetting it
/// produces files that open in one decoder and are truncated in another, which is a miserable
/// bug to find later.
/// </remarks>
sealed class JpegBitWriter(Stream output)
{
    int accumulator;
    int pending;

    /// <summary>Writes the code for a symbol from a table.</summary>
    public void WriteCode(JpegHuffmanTable table, int symbol)
    {
        var length = table.Length(symbol);
        if (length == 0)
            throw new InvalidOperationException($"Symbol {symbol} has no code in this Huffman table.");

        this.WriteBits(table.Code(symbol), length);
    }

    /// <summary>
    /// Writes a coefficient in the format's signed representation.
    /// </summary>
    /// <remarks>
    /// Positive values are their own low bits. Negative values are stored as the value minus
    /// one, which in <paramref name="size"/> bits is the one's complement - so the decoder can
    /// tell the sign from the leading bit without a sign bit being spent on it.
    /// </remarks>
    public void WriteValue(int value, int size)
    {
        if (size == 0)
            return;

        var encoded = value < 0 ? value - 1 : value;
        this.WriteBits(encoded & ((1 << size) - 1), size);
    }

    /// <summary>
    /// Pads the final byte with one bits and flushes it.
    /// </summary>
    /// <remarks>
    /// One bits rather than zeros: a run of ones cannot begin a valid code in these tables, so
    /// a decoder reading past the end of the last real code sees padding rather than a
    /// spurious extra coefficient.
    /// </remarks>
    public void Flush()
    {
        while (this.pending > 0)
            this.WriteBits(1, 1);
    }

    void WriteBits(int code, int length)
    {
        for (var i = length - 1; i >= 0; i--)
        {
            this.accumulator = (this.accumulator << 1) | ((code >> i) & 1);
            this.pending++;

            if (this.pending != 8)
                continue;

            var complete = (byte)this.accumulator;
            output.WriteByte(complete);

            if (complete == 0xFF)
                output.WriteByte(0x00);

            this.accumulator = 0;
            this.pending = 0;
        }
    }
}
