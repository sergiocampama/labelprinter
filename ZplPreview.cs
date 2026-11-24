using System.Text;
using BinaryKits.Zpl.Viewer;

namespace LabelPrinter;

/// <summary>Draws ZPL labels locally (nothing leaves the PC) for the preview window.</summary>
internal static class ZplPreview
{
    public static List<LabelPreview> Render(byte[] data, double widthMm, double heightMm, int dotsPerMm)
    {
        // Printers ignore CR/LF between commands, but the renderer's parser
        // trips on them and silently drops later text fields.
        var zpl = Encoding.UTF8.GetString(data).Replace("\r", "").Replace("\n", "");
        var copies = DocumentType.CopiesPerFormat(zpl);

        IPrinterStorage storage = new PrinterStorage();
        var analyzer = new ZplAnalyzer(storage);
        var drawer = new ZplElementDrawer(storage);

        var result = new List<LabelPreview>();
        var labels = analyzer.Analyze(zpl).LabelInfos;
        for (int i = 0; i < labels.Length; i++)
        {
            var png = drawer.Draw(labels[i].ZplElements, widthMm, heightMm, dotsPerMm);
            using var stream = new MemoryStream(png);
            using var drawn = new Bitmap(stream);

            // The renderer leaves the background transparent: put it on white paper.
            var page = new Bitmap(drawn.Width, drawn.Height);
            using (var g = Graphics.FromImage(page))
            {
                g.Clear(Color.White);
                g.DrawImage(drawn, 0, 0, drawn.Width, drawn.Height);
            }
            result.Add(new LabelPreview(page, i < copies.Count ? copies[i] : 1));
        }
        return result;
    }
}
