namespace LabelPrinter;

/// <summary>Shows the labels a file will print, one under the other, at label proportions.</summary>
internal sealed class PreviewForm : Form
{
    private const int LabelDisplayWidth = 380;

    public PreviewForm(string fileName, DocumentType type, IReadOnlyList<LabelPreview> labels)
    {
        Text = $"Vista previa: {fileName}";
        Icon = AppInfo.LoadIcon(new Size(32, 32));
        Font = new Font("Segoe UI", 10F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(LabelDisplayWidth + 60, 720);
        MinimizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        int totalCopies = labels.Sum(l => l.Copies);
        var header = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 52,
            Padding = new Padding(16, 10, 16, 0),
            Text = $"{type.Name}\n{Count(labels.Count, "etiqueta distinta", "etiquetas distintas")}, " +
                   $"{Count(totalCopies, "copia en total", "copias en total")}.",
            ForeColor = SystemColors.GrayText,
        };

        var list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16, 8, 16, 16),
            BackColor = Color.FromArgb(232, 234, 237),
        };

        for (int i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            int height = (int)Math.Round(LabelDisplayWidth * label.Image.Height / (double)label.Image.Width);
            list.Controls.Add(new Label
            {
                AutoSize = true,
                Margin = new Padding(0, i == 0 ? 0 : 14, 0, 4),
                Text = labels.Count == 1 && label.Copies == 1
                    ? "Etiqueta"
                    : $"Etiqueta {i + 1} de {labels.Count}" + (label.Copies > 1 ? $"  ×{label.Copies} copias" : ""),
            });
            list.Controls.Add(new PictureBox
            {
                Image = label.Image,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(LabelDisplayWidth, height),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0),
            });
        }

        Controls.Add(list);
        Controls.Add(header);
    }

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var picture in Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<PictureBox>()))
                picture.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
