using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace SimplePrint.Service;

/// <summary>
/// Druckt eine PDF über die normale Windows-Warteschlange des Servers.
/// Die Seiten werden mit der Windows-eigenen PDF-Funktion (Windows.Data.Pdf) in
/// Bilder umgewandelt und über den Druckertreiber des Servers ausgegeben.
/// Der Treiber des Servers wählt dadurch selbst das Format, das der Drucker versteht,
/// und die Druckeinstellungen der Warteschlange (Duplex, Farbe, Qualität) gelten.
/// </summary>
internal static class PdfPrinter
{
    private const double RenderDpi = 300.0;
    private const int MaxRenderPixels = 6000;
    private const uint MaxPages = 500;

    private sealed record RenderedPage(byte[] Png, bool Landscape);

    public static async Task PrintAsync(
        string queueName,
        string pdfPath,
        string documentName,
        CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(pdfPath, ct);
        var pages = await RenderPagesAsync(bytes, ct);

        await Task.Run(() => PrintRendered(queueName, documentName, pages), ct);
    }

    private static async Task<List<RenderedPage>> RenderPagesAsync(
        byte[] pdfBytes,
        CancellationToken ct)
    {
        using var input = new InMemoryRandomAccessStream();

        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(pdfBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        input.Seek(0);

        PdfDocument document;
        try
        {
            document = await PdfDocument.LoadFromStreamAsync(input);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                "Die PDF-Datei konnte nicht gelesen werden (beschädigt oder unvollständig übertragen).",
                ex);
        }

        if (document.IsPasswordProtected)
            throw new InvalidDataException("Die PDF-Datei ist passwortgeschützt.");

        if (document.PageCount == 0)
            throw new InvalidDataException("Die PDF-Datei enthält keine Seiten.");

        if (document.PageCount > MaxPages)
            throw new InvalidDataException(
                $"Die PDF-Datei hat {document.PageCount} Seiten (erlaubt sind {MaxPages}).");

        var pages = new List<RenderedPage>((int)document.PageCount);

        for (uint i = 0; i < document.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var page = document.GetPage(i);

            var width = page.Size.Width * RenderDpi / 96.0;
            var height = page.Size.Height * RenderDpi / 96.0;

            var longest = Math.Max(width, height);
            if (longest > MaxRenderPixels)
            {
                var factor = MaxRenderPixels / longest;
                width *= factor;
                height *= factor;
            }

            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(width)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(height))
            };

            using var output = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(output, options);

            var size = (uint)output.Size;
            if (size == 0)
                throw new InvalidDataException($"Seite {i + 1} konnte nicht gerendert werden.");

            var png = new byte[size];
            using (var reader = new DataReader(output.GetInputStreamAt(0)))
            {
                await reader.LoadAsync(size);
                reader.ReadBytes(png);
            }

            pages.Add(new RenderedPage(png, page.Size.Width > page.Size.Height));
        }

        return pages;
    }

    private static void PrintRendered(
        string queueName,
        string documentName,
        List<RenderedPage> pages)
    {
        using var document = new PrintDocument
        {
            DocumentName = documentName,
            // Ohne eigenen Controller würde Windows einen Fortschrittsdialog öffnen,
            // den es in einem Dienst nicht anzeigen kann.
            PrintController = new StandardPrintController()
        };

        document.PrinterSettings.PrinterName = queueName;

        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException(
                $"Der Windows-Drucker '{queueName}' ist für den PDF-Druck nicht verwendbar.");

        var index = 0;

        document.QueryPageSettings += (_, e) =>
        {
            if (index < pages.Count)
                e.PageSettings.Landscape = pages[index].Landscape;
        };

        document.PrintPage += (_, e) =>
        {
            var rendered = pages[index];

            using var stream = new MemoryStream(rendered.Png);
            using var image = Image.FromStream(stream);

            var graphics = e.Graphics!;

            // Der Ursprung liegt am bedruckbaren Bereich, die Seite aber am Papierrand.
            graphics.TranslateTransform(
                -e.PageSettings.HardMarginX,
                -e.PageSettings.HardMarginY);

            var bounds = e.PageBounds;
            var scale = Math.Min(
                (float)bounds.Width / image.Width,
                (float)bounds.Height / image.Height);

            var width = image.Width * scale;
            var height = image.Height * scale;
            var x = bounds.Left + (bounds.Width - width) / 2f;
            var y = bounds.Top + (bounds.Height - height) / 2f;

            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, new RectangleF(x, y, width, height));

            index++;
            e.HasMorePages = index < pages.Count;
        };

        document.Print();
    }
}
