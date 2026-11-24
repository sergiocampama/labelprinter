using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LabelPrinter;

/// <summary>
/// Turns each page of a PDF into a label-sized black-and-white image: finds the
/// printed area on the page (e.g. the bordered box of a shipping label), then
/// renders just that area again, at the printer's resolution, as large as fits
/// inside the label (turned 90° when that fits better). Uses the PDF renderer
/// built into Windows.
/// </summary>
internal static class PdfLabelRenderer
{
    // Resolution of the first pass that only locates the printed area.
    private const double ProbeWidthPixels = 1200;

    /// <param name="sideMargin">Blank dots kept at the left and right edges.</param>
    /// <param name="endMargin">Blank dots kept at the top and bottom edges.</param>
    public static async Task<List<MonoImage>> RenderAsync(string path, int labelWidth, int labelHeight, int sideMargin, int endMargin)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var document = await PdfDocument.LoadFromFileAsync(file);

        var labels = new List<MonoImage>();
        for (uint i = 0; i < document.PageCount; i++)
        {
            using var page = document.GetPage(i);
            var label = await RenderPageAsync(page, labelWidth, labelHeight, sideMargin, endMargin);
            if (label is not null)
                labels.Add(label);
        }
        return labels;
    }

    private static async Task<MonoImage?> RenderPageAsync(PdfPage page, int labelWidth, int labelHeight, int sideMargin, int endMargin)
    {
        // Page.Size is in DIPs (1/96 inch).
        var pageSize = page.Size;

        // 1. Find the printed area.
        double probeScale = ProbeWidthPixels / pageSize.Width;
        using var probe = await RenderAsync(page, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Round(pageSize.Width * probeScale),
            DestinationHeight = (uint)Math.Round(pageSize.Height * probeScale),
        });
        var ink = InkBounds(MonoImage.FromBitmap(probe, threshold: 200));
        if (ink is null)
            return null; // blank page

        var area = new Rect(
            Math.Max(0, (ink.Value.Left - 1) / probeScale),
            Math.Max(0, (ink.Value.Top - 1) / probeScale),
            (ink.Value.Width + 2) / probeScale,
            (ink.Value.Height + 2) / probeScale);
        area.Width = Math.Min(area.Width, pageSize.Width - area.X);
        area.Height = Math.Min(area.Height, pageSize.Height - area.Y);

        // 2. Fit it into the label, upright or turned, whichever is larger.
        // (Margins stay with the label's edges whether or not the content is turned.)
        double availableWidth = labelWidth - 2 * sideMargin, availableHeight = labelHeight - 2 * endMargin;
        double upright = Math.Min(availableWidth / area.Width, availableHeight / area.Height);
        double turned = Math.Min(availableWidth / area.Height, availableHeight / area.Width);
        bool rotate = turned > upright * 1.05;
        double scale = rotate ? turned : upright;

        // 3. Render only that area at its final size, then make it black and white.
        using var bitmap = await RenderAsync(page, new PdfPageRenderOptions
        {
            SourceRect = area,
            DestinationWidth = (uint)Math.Max(1, Math.Floor(area.Width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Floor(area.Height * scale)),
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
        });
        var content = MonoImage.FromBitmap(bitmap);
        if (rotate)
            content = content.RotateClockwise();

        // 4. Center it on the label.
        var label = new MonoImage(labelWidth, labelHeight);
        label.Draw(content, (labelWidth - content.Width) / 2, (labelHeight - content.Height) / 2);
        return label;
    }

    private static async Task<Bitmap> RenderAsync(PdfPage page, PdfPageRenderOptions options)
    {
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
        using var managed = stream.AsStreamForRead();
        using var decoded = new Bitmap(managed);
        return new Bitmap(decoded); // detach from the stream
    }

    private static Rectangle? InkBounds(MonoImage image)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (!image[x, y]) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                bottom = y;
            }
        }
        return right < 0 ? null : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
}
