using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Printers.Client;
using Shiny.Printers.Document;
using Shiny.Printers.Imaging;
using SkiaSharp;
using Native = Shiny.Printers;

namespace Shiny.AppDeviceBridge.Printers;

/// <summary>An element the page sent that cannot be printed — answered with 400, naming the element.</summary>
sealed class PrintElementException(int index, string message) : Exception($"elements[{index}]: {message}");

/// <summary>Turns the page's elements into a <see cref="PrintDocument"/>, checking each against the printer it is for.</summary>
static class PrintElementMapper
{
    public const int MaxElements = 2_000;
    const int MaxTextLength = 4_096;
    const int MaxRawBytes = 64 * 1024;
    const int MaxImageBytes = 16 * 1024 * 1024;
    const int MaxImagePixels = 4096 * 4096;

    /// <param name="forPrinter">
    /// True for a printer, which refuses what it cannot print; false for a PDF, which can draw a barcode any printer could.
    /// </param>
    public static PrintDocument ToDocument(IReadOnlyList<PrintElement>? elements, Native.PrinterCapabilities capabilities, bool forPrinter)
    {
        if (elements is null || elements.Count == 0)
            throw new PrintElementException(0, "a receipt needs at least one element.");

        if (elements.Count > MaxElements)
            throw new PrintElementException(MaxElements, $"a receipt is at most {MaxElements} elements.");

        var document = new PrintDocument();
        for (var i = 0; i < elements.Count; i++)
        {
            var e = elements[i] ?? throw new PrintElementException(i, "is null.");
            Add(document, e, i, capabilities, forPrinter);
        }
        return document;
    }

    static void Add(PrintDocument document, PrintElement e, int i, Native.PrinterCapabilities capabilities, bool forPrinter)
    {
        switch (e.Type)
        {
            case PrintElementType.Text:
                document.Text(Text(e, i, required: true));
                break;

            case PrintElementType.Line:
                document.Line(Text(e, i, required: false));
                break;

            case PrintElementType.Align:
                document.Align(BridgeEnum.Convert<PrintAlign, Native.PrintAlignment>(e.Align ?? throw new PrintElementException(i, "align needs an align.")));
                break;

            case PrintElementType.Bold:
                document.Bold(e.On ?? true);
                break;

            case PrintElementType.Underline:
                document.Underline(e.On ?? true);
                break;

            case PrintElementType.Size:
                var width = e.Width ?? throw new PrintElementException(i, "size needs a width.");
                var height = e.Height ?? width;
                if (width is < 1 or > 8 || height is < 1 or > 8)
                    throw new PrintElementException(i, "size width and height are 1 to 8.");
                document.Size(width, height);
                break;

            case PrintElementType.ResetStyle:
                document.ResetStyle();
                break;

            case PrintElementType.Feed:
                document.Feed(Range(e.Lines ?? 1, 1, 255, i, "feed lines"));
                break;

            case PrintElementType.Barcode:
                var format = e.Barcode ?? throw new PrintElementException(i, "barcode needs a barcode format.");
                var nativeFormat = BridgeEnum.Convert<PrintBarcodeFormat, Native.BarcodeFormat>(format);
                if (forPrinter && !capabilities.Supports(nativeFormat))
                    throw new PrintElementException(i, $"this printer does not print {format} barcodes.");

                document.Barcode(
                    nativeFormat,
                    Text(e, i, required: true),
                    Range(e.Height ?? 80, 1, 255, i, "barcode height"),
                    Range(e.Width ?? 2, 2, 6, i, "barcode width"),
                    BridgeEnum.Convert<PrintBarcodeText, Native.BarcodeTextPosition>(e.BarcodeText ?? PrintBarcodeText.Below)
                );
                break;

            case PrintElementType.QrCode:
                if (forPrinter && !capabilities.SupportsQrCodes)
                    throw new PrintElementException(i, "this printer does not print QR codes.");

                document.QrCode(
                    Text(e, i, required: true),
                    Range(e.ModuleSize ?? 6, 1, 16, i, "qrCode moduleSize"),
                    BridgeEnum.Convert<PrintQrCorrection, Native.QrCorrectionLevel>(e.Correction ?? PrintQrCorrection.Medium)
                );
                break;

            case PrintElementType.Image:
                if (forPrinter && !capabilities.SupportsImages)
                    throw new PrintElementException(i, "this printer does not print images.");

                document.Image(Image(Base64(e, i, MaxImageBytes), capabilities.DotsPerLine, e.Dithering ?? PrintDithering.Threshold, i));
                break;

            case PrintElementType.Cut:
                var feed = Range(e.Lines ?? 3, 0, 255, i, "cut lines");

                // A receipt is often written for whichever printer is connected; one without a cutter still feeds the
                // paper out to tear.
                if (capabilities.SupportsCut)
                    document.Cut(BridgeEnum.Convert<PrintCutMode, Native.CutMode>(e.Cut ?? PrintCutMode.Partial), feed);
                else if (feed > 0)
                    document.Feed(feed);
                break;

            case PrintElementType.Raw:
                document.Raw(Base64(e, i, MaxRawBytes));
                break;

            default:
                throw new PrintElementException(i, $"unknown type '{e.Type}'.");
        }
    }

    static string Text(PrintElement e, int i, bool required)
    {
        if (required && String.IsNullOrEmpty(e.Text))
            throw new PrintElementException(i, $"{Name(e.Type)} needs text.");

        if (e.Text is { Length: > MaxTextLength })
            throw new PrintElementException(i, $"text is at most {MaxTextLength} characters.");

        return e.Text ?? String.Empty;
    }

    static int Range(int value, int min, int max, int i, string what)
        => value >= min && value <= max ? value : throw new PrintElementException(i, $"{what} is {min} to {max}.");

    static byte[] Base64(PrintElement e, int i, int maxBytes)
    {
        if (String.IsNullOrEmpty(e.Data))
            throw new PrintElementException(i, $"{Name(e.Type)} needs base64 data.");

        if (e.Data.Length / 4 * 3 > maxBytes)
            throw new PrintElementException(i, $"{Name(e.Type)} data is at most {maxBytes} bytes.");

        try
        {
            return Convert.FromBase64String(e.Data);
        }
        catch (FormatException)
        {
            throw new PrintElementException(i, $"{Name(e.Type)} data is not base64.");
        }
    }

    /// <summary>Decodes a PNG or JPEG, scales it down to <paramref name="maxWidth"/> dots when wider, and dithers it to black and white.</summary>
    static PrinterImage Image(byte[] data, int maxWidth, PrintDithering dithering, int i)
    {
        using var codec = SKCodec.Create(new MemoryStream(data))
            ?? throw new PrintElementException(i, "image data is not a PNG or JPEG.");

        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width < 1 || height < 1 || (long)width * height > MaxImagePixels)
            throw new PrintElementException(i, $"an image is at most {MaxImagePixels} pixels.");

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var decoded = new SKBitmap(info);
        if (codec.GetPixels(info, decoded.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new PrintElementException(i, "image data could not be decoded.");

        var bitmap = decoded;
        SKBitmap? scaled = null;
        if (width > maxWidth)
        {
            var target = new SKImageInfo(maxWidth, Math.Max(1, (int)Math.Round(height * (double)maxWidth / width)), SKColorType.Rgba8888, SKAlphaType.Unpremul);
            bitmap = scaled = decoded.Resize(target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
                ?? throw new PrintElementException(i, "image could not be scaled to the printer's width.");
        }

        using (scaled)
        {
            return PrinterImage.FromPixels(
                bitmap.GetPixelSpan(),
                bitmap.Width,
                bitmap.Height,
                BridgeEnum.Convert<PrintDithering, Native.ImageDithering>(dithering)
            );
        }
    }

    static string Name(PrintElementType type) => type switch
    {
        PrintElementType.QrCode => "qrCode",
        PrintElementType.ResetStyle => "resetStyle",
        _ => type.ToString().ToLowerInvariant()
    };
}
