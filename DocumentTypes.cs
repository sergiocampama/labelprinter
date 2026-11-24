using System.Text;
using System.Text.RegularExpressions;

namespace LabelPrinter;

/// <summary>What a document type expects to receive.</summary>
internal enum SourceFormat
{
    /// <summary>ZPL label commands, sent to the printer with the label size added.</summary>
    Zpl,

    /// <summary>A PDF whose printed area is converted to a label image (one label per page).</summary>
    Pdf,
}

/// <summary>A rendered label for the preview, and how many copies the file asks for.</summary>
internal sealed record LabelPreview(Bitmap Image, int Copies);

/// <summary>Thrown when a file doesn't match the chosen document type; the message is for the user.</summary>
internal sealed class WrongFileException(string message) : Exception(message);

/// <summary>
/// A kind of document to print, the label it goes on, and how its file becomes
/// printer data. Labels are WidthDots x HeightDots at 203 dpi (8 dots/mm).
/// </summary>
internal sealed record DocumentType(string Id, string Name, string Paper, SourceFormat Format, int WidthDots, int HeightDots)
{
    public const int DotsPerMm = 8;

    // Blank border kept around a converted PDF: 5 mm at the sides, 3 mm at the
    // top and bottom. Wide enough that nothing runs off the label edge even
    // when the label sits slightly off-center under the print head.
    private const int PdfSideMarginDots = 40, PdfEndMarginDots = 24;

    public override string ToString() => Name;

    public double WidthMm => WidthDots / (double)DotsPerMm;
    public double HeightMm => HeightDots / (double)DotsPerMm;

    /// <summary>The bytes to send to the printer for this file.</summary>
    public async Task<byte[]> BuildPrintDataAsync(string path)
    {
        var file = await File.ReadAllBytesAsync(path);
        switch (Format)
        {
            case SourceFormat.Zpl:
                RequireZpl(file);
                return AddLabelSize(file);

            default:
                RequirePdf(file);
                var labels = await RenderPdfAsync(path);
                var zpl = new StringBuilder();
                foreach (var label in labels)
                    zpl.Append(label.ToZplLabel());
                return Encoding.ASCII.GetBytes(zpl.ToString());
        }
    }

    /// <summary>What the labels will look like, one image per label.</summary>
    public async Task<List<LabelPreview>> BuildPreviewAsync(string path)
    {
        var file = await File.ReadAllBytesAsync(path);
        switch (Format)
        {
            case SourceFormat.Zpl:
                RequireZpl(file);
                var data = AddLabelSize(file);
                return await Task.Run(() => ZplPreview.Render(data, WidthMm, HeightMm, DotsPerMm));

            default:
                RequirePdf(file);
                var labels = await RenderPdfAsync(path);
                return labels.Select(l => new LabelPreview(l.ToBitmap(), 1)).ToList();
        }
    }

    private async Task<List<MonoImage>> RenderPdfAsync(string path)
    {
        var labels = await PdfLabelRenderer.RenderAsync(path, WidthDots, HeightDots, PdfSideMarginDots, PdfEndMarginDots);
        if (labels.Count == 0)
            throw new WrongFileException("El PDF no tiene nada que imprimir (las páginas están en blanco).");
        return labels;
    }

    /// <summary>
    /// Inserts the label size right after each ^XA (start of a label format), so
    /// the label prints at the right size whatever the printer was last set to.
    /// Size commands in the file come later in the format and still win.
    /// </summary>
    private byte[] AddLabelSize(byte[] file)
    {
        var setup = Encoding.ASCII.GetBytes($"^PW{WidthDots}^LL{HeightDots}");
        using var output = new MemoryStream(file.Length + 64);
        int start = 0;
        for (int i = 0; i + 2 < file.Length; i++)
        {
            if (IsXa(file, i))
            {
                output.Write(file, start, i + 3 - start);
                output.Write(setup);
                start = i + 3;
                i += 2;
            }
        }
        output.Write(file, start, file.Length - start);
        return output.ToArray();
    }

    private static bool IsXa(byte[] data, int i) =>
        data[i] == (byte)'^' && (data[i + 1] | 0x20) == 'x' && (data[i + 2] | 0x20) == 'a';

    private void RequireZpl(byte[] file)
    {
        for (int i = 0; i + 2 < file.Length; i++)
            if (IsXa(file, i))
                return;
        throw new WrongFileException(
            $"Este archivo no es una etiqueta ZPL, así que no se puede imprimir como «{Name}». " +
            "Elige el archivo .txt de etiquetas, o el tipo de documento que corresponda.");
    }

    private void RequirePdf(byte[] file)
    {
        if (file.Length >= 5 && Encoding.ASCII.GetString(file, 0, 5) == "%PDF-")
            return;
        throw new WrongFileException($"Este archivo no es un PDF, así que no se puede imprimir como «{Name}».");
    }

    /// <summary>The ^PQ (print quantity) of each label format in a ZPL file, 1 when absent.</summary>
    public static List<int> CopiesPerFormat(string zpl)
    {
        var result = new List<int>();
        foreach (Match format in Regex.Matches(zpl, @"\^XA(.*?)\^XZ", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var pq = Regex.Match(format.Groups[1].Value, @"\^PQ(\d+)", RegexOptions.IgnoreCase);
            result.Add(pq.Success && int.TryParse(pq.Groups[1].Value, out int n) && n > 0 ? n : 1);
        }
        return result;
    }
}

internal static class DocumentTypes
{
    // 100 x 150 mm label (4 x 6 in) at 8 dots/mm.
    private const int Width100 = 812, Height150 = 1218;

    public static readonly IReadOnlyList<DocumentType> All =
    [
        new("meli-full-100x150",
            "Mercado Libre Full (100x150)",
            "Papel: etiqueta térmica de 100 × 150 mm. Archivo: etiquetas ZPL (.txt) de Mercado Libre; " +
            "se imprimen a ese tamaño sin importar cómo esté configurada la impresora.",
            SourceFormat.Zpl, Width100, Height150),
        new("pdf-100x150",
            "Etiqueta PDF (100x150)",
            "Papel: etiqueta térmica de 100 × 150 mm. Archivo: etiqueta en PDF (Ninja Hubs, Blue Express u otra). " +
            "Se detecta lo impreso en cada página y se ajusta al tamaño de la etiqueta.",
            SourceFormat.Pdf, Width100, Height150),
    ];

    // Types that were merged into another; old saved choices map to the new one.
    private static readonly Dictionary<string, string> Renamed = new()
    {
        ["ninja-pdf-100x150"] = "pdf-100x150",
        ["blue-pdf-100x150"] = "pdf-100x150",
    };

    public static DocumentType? Find(string? id)
    {
        if (id is not null && Renamed.TryGetValue(id, out var current))
            id = current;
        return All.FirstOrDefault(t => t.Id == id);
    }
}
