namespace Shiny.AppDeviceBridge.Printers.Client;

/// <summary>
/// Builds a receipt the way <c>Shiny.Printers.Document.PrintDocument</c> does, as the elements the bridge takes.
/// <code>
/// var status = await printers.GetStatusAsync();
/// var columns = status.Printer!.Capabilities.CharactersPerLine;
///
/// var receipt = new PrintElements()
///     .AlignCenter().Bold().Size(2).Line("SHINY MART").ResetStyle()
///     .Line(new string('-', columns))
///     .QrCode("https://example.com/r/100425")
///     .Feed(2)
///     .Cut();
///
/// await printers.PrintAsync(receipt.ToRequest());
/// </code>
/// </summary>
public sealed class PrintElements
{
    readonly List<PrintElement> elements = [];

    public IReadOnlyList<PrintElement> Elements => this.elements;

    public PrintElements Add(PrintElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        this.elements.Add(element);
        return this;
    }

    public PrintElements Align(PrintAlign align) => this.Add(new(PrintElementType.Align, Align: align));
    public PrintElements AlignLeft() => this.Align(PrintAlign.Left);
    public PrintElements AlignCenter() => this.Align(PrintAlign.Center);
    public PrintElements AlignRight() => this.Align(PrintAlign.Right);
    public PrintElements Bold(bool on = true) => this.Add(new(PrintElementType.Bold, On: on));
    public PrintElements Underline(bool on = true) => this.Add(new(PrintElementType.Underline, On: on));

    /// <summary>1–8 times the normal character width and height.</summary>
    public PrintElements Size(int width, int height) => this.Add(new(PrintElementType.Size, Width: width, Height: height));
    public PrintElements Size(int scale) => this.Size(scale, scale);
    public PrintElements ResetStyle() => this.Add(new(PrintElementType.ResetStyle));

    public PrintElements Text(string text) => this.Add(new(PrintElementType.Text, Text: text));
    public PrintElements Line(string text = "") => this.Add(new(PrintElementType.Line, Text: text));
    public PrintElements Feed(int lines = 1) => this.Add(new(PrintElementType.Feed, Lines: lines));

    public PrintElements Barcode(PrintBarcodeFormat format, string data, int height = 80, int moduleWidth = 2, PrintBarcodeText text = PrintBarcodeText.Below)
        => this.Add(new(PrintElementType.Barcode, Text: data, Barcode: format, Height: height, Width: moduleWidth, BarcodeText: text));

    public PrintElements QrCode(string data, int moduleSize = 6, PrintQrCorrection correction = PrintQrCorrection.Medium)
        => this.Add(new(PrintElementType.QrCode, Text: data, ModuleSize: moduleSize, Correction: correction));

    /// <summary>A PNG or JPEG, scaled down to the printer's width when wider.</summary>
    public PrintElements Image(byte[] image, PrintDithering dithering = PrintDithering.Threshold)
        => this.Add(new(PrintElementType.Image, Data: Convert.ToBase64String(image), Dithering: dithering));

    public PrintElements Cut(PrintCutMode mode = PrintCutMode.Partial, int feedBefore = 3)
        => this.Add(new(PrintElementType.Cut, Cut: mode, Lines: feedBefore));

    /// <summary>Bytes sent to the printer as they are.</summary>
    public PrintElements Raw(byte[] data) => this.Add(new(PrintElementType.Raw, Data: Convert.ToBase64String(data)));

    public PrintReceiptRequest ToRequest() => new([.. this.elements]);
}
