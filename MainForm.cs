namespace LabelPrinter;

/// <summary>
/// The window: pick a connected USB printer, a document type and a file, then
/// preview it or send it to the printer.
/// </summary>
internal sealed class MainForm : Form
{
    private const int WM_DEVICECHANGE = 0x0219;

    private readonly Settings _settings = Settings.Load();
    private readonly PrinterChangeWatcher _watcher = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    // Backstop while the window is open, in case a plug/unplug notification is missed.
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 3000 };
    private int _followUpRefreshes;

    private readonly ComboBox _printerCombo;
    private readonly ComboBox _typeCombo;
    private readonly Label _paperLabel;
    private readonly TextBox _fileBox;
    private readonly Button _browseButton;
    private readonly Button _printButton;
    private readonly Button _previewButton;
    private readonly Label _statusLabel;

    private string? _filePath;
    private bool _busy;

    public MainForm()
    {
        Text = AppInfo.Name;
        Icon = AppInfo.LoadIcon(new Size(32, 32));
        Font = new Font("Segoe UI", 10F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 430);
        AllowDrop = true;

        var muted = SystemColors.GrayText;

        _printerCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _printerCombo.SelectionChangeCommitted += (_, _) =>
        {
            if (_printerCombo.SelectedItem is UsbPrinter p) { _settings.Printer = p.Name; _settings.Save(); }
            UpdateState();
        };

        _typeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        foreach (var t in DocumentTypes.All)
            _typeCombo.Items.Add(t);
        _typeCombo.SelectedItem = DocumentTypes.Find(_settings.DocumentType) ?? DocumentTypes.All[0];
        _typeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_typeCombo.SelectedItem is DocumentType t) { _settings.DocumentType = t.Id; _settings.Save(); }
            UpdatePaper();
            UpdateState();
        };

        _paperLabel = new Label { AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = muted, Margin = new Padding(3, 4, 3, 0) };

        _fileBox = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, PlaceholderText = "Ningún archivo elegido" };
        _browseButton = new Button { Text = "Elegir archivo…", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        _browseButton.Click += (_, _) => BrowseFile();

        var fileRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fileRow.Controls.Add(_fileBox, 0, 0);
        fileRow.Controls.Add(_browseButton, 1, 0);

        _printButton = new Button
        {
            Text = "Imprimir",
            AutoSize = false,
            Size = new Size(160, 40),
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Margin = new Padding(8, 0, 0, 0),
        };
        _printButton.Click += async (_, _) => await PrintAsync();
        AcceptButton = _printButton;

        _previewButton = new Button
        {
            Text = "Vista previa",
            AutoSize = false,
            Size = new Size(140, 40),
            Margin = new Padding(0),
        };
        _previewButton.Click += async (_, _) => await PreviewAsync();

        var buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(3, 18, 3, 3),
        };
        buttonRow.Controls.Add(_printButton);
        buttonRow.Controls.Add(_previewButton);

        _statusLabel = new Label { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(3, 8, 3, 0) };

        var version = new Label
        {
            Text = $"{AppInfo.Name} v{AppInfo.Version}",
            AutoSize = true,
            ForeColor = muted,
            Font = new Font("Segoe UI", 8.5F),
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16, 12, 16, 10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control c, bool fill = false)
        {
            layout.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(c);
        }
        Add(Heading("Impresora"));
        Add(_printerCombo);
        Add(Hint("Solo impresoras conectadas por USB. La lista se actualiza sola al conectar o desconectar una."));
        Add(Heading("Tipo de documento"));
        Add(_typeCombo);
        Add(_paperLabel);
        Add(Heading("Archivo"));
        Add(fileRow);
        Add(buttonRow);
        Add(_statusLabel, fill: true);
        Add(version);
        Controls.Add(layout);

        // Refresh shortly after a change, then again a bit later: Windows
        // creates the print queue a moment after the device shows up.
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshPrinters();
            if (_followUpRefreshes-- > 0)
            {
                _refreshTimer.Interval = 2500;
                _refreshTimer.Start();
            }
        };
        _watcher.Changed += () =>
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(ScheduleRefresh);
        };
        _watcher.Start();

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 })
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && File.Exists(files[0]))
                SetFile(files[0]);
        };

        _pollTimer.Tick += (_, _) => RefreshPrinters();
        VisibleChanged += (_, _) =>
        {
            if (Visible) { RefreshPrinters(); _pollTimer.Start(); }
            else _pollTimer.Stop();
        };
        FormClosing += (_, e) =>
        {
            // Closing the window keeps the app in the tray.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };

        UpdatePaper();
        RefreshPrinters();
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 10F, FontStyle.Bold),
        Margin = new Padding(3, 14, 3, 4),
    };

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(520, 0),
        ForeColor = SystemColors.GrayText,
        Font = new Font("Segoe UI", 8.5F),
        Margin = new Padding(3, 4, 3, 0),
    };

    protected override void WndProc(ref Message m)
    {
        // USB plug/unplug (DBT_DEVNODES_CHANGED, arrival, removal).
        if (m.Msg == WM_DEVICECHANGE)
            ScheduleRefresh();
        base.WndProc(ref m);
    }

    private void ScheduleRefresh()
    {
        _followUpRefreshes = 2;
        _refreshTimer.Stop();
        _refreshTimer.Interval = 600;
        _refreshTimer.Start();
    }

    private void RefreshPrinters()
    {
        List<UsbPrinter> printers;
        try { printers = UsbPrinters.Find(); }
        catch { printers = []; }

        var current = _printerCombo.Items.OfType<UsbPrinter>().ToList();
        bool showingNone = _printerCombo.Items.Count > 0 && current.Count == 0;
        if (current.Count > 0 ? current.SequenceEqual(printers) : showingNone && printers.Count == 0)
            return; // unchanged (also keeps an open dropdown from flickering on each poll)

        var keep = (_printerCombo.SelectedItem as UsbPrinter)?.Name ?? _settings.Printer;

        _printerCombo.BeginUpdate();
        _printerCombo.Items.Clear();
        if (printers.Count == 0)
        {
            _printerCombo.Items.Add("No hay impresoras USB conectadas");
            _printerCombo.SelectedIndex = 0;
            _printerCombo.Enabled = false;
        }
        else
        {
            foreach (var p in printers)
                _printerCombo.Items.Add(p);
            _printerCombo.Enabled = true;
            _printerCombo.SelectedItem = printers.FirstOrDefault(p => p.Name == keep) ?? printers[0];
        }
        _printerCombo.EndUpdate();
        UpdateState();
    }

    private void UpdatePaper()
    {
        _paperLabel.Text = (_typeCombo.SelectedItem as DocumentType)?.Paper ?? "";
    }

    private void BrowseFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Elegir archivo para imprimir",
            Filter = "Todos los archivos (*.*)|*.*|Etiquetas ZPL (*.zpl;*.txt)|*.zpl;*.txt|PDF (*.pdf)|*.pdf",
            Multiselect = false,
            InitialDirectory = Directory.Exists(_settings.LastFolder)
                ? _settings.LastFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SetFile(dialog.FileName);
    }

    private void SetFile(string path)
    {
        _filePath = path;
        _fileBox.Text = path;
        _fileBox.SelectionStart = _fileBox.Text.Length;
        _settings.LastFolder = Path.GetDirectoryName(path);
        _settings.Save();
        SetStatus("", SystemColors.ControlText);
        UpdateState();
    }

    private void UpdateState()
    {
        bool haveFile = _typeCombo.SelectedItem is DocumentType && !string.IsNullOrEmpty(_filePath);
        _printButton.Enabled = !_busy && haveFile && _printerCombo.SelectedItem is UsbPrinter;
        _previewButton.Enabled = !_busy && haveFile; // previewing needs no printer
        _browseButton.Enabled = !_busy;
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private async Task PreviewAsync()
    {
        if (_busy || _typeCombo.SelectedItem is not DocumentType type || _filePath is null)
            return;

        var fileName = Path.GetFileName(_filePath);
        _busy = true;
        UpdateState();
        SetStatus($"Preparando la vista previa de {fileName}…", SystemColors.ControlText);
        try
        {
            var labels = await type.BuildPreviewAsync(_filePath);
            SetStatus("", SystemColors.ControlText);
            using var preview = new PreviewForm(fileName, type, labels);
            preview.ShowDialog(this);
        }
        catch (Exception ex)
        {
            SetStatus(Describe(ex, "No se pudo mostrar la vista previa"), Color.Firebrick);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    private async Task PrintAsync()
    {
        if (_busy || _printerCombo.SelectedItem is not UsbPrinter printer
            || _typeCombo.SelectedItem is not DocumentType type || _filePath is null)
            return;

        var fileName = Path.GetFileName(_filePath);
        _busy = true;
        UpdateState();
        SetStatus($"Enviando {fileName} a «{printer.Name}»…", SystemColors.ControlText);
        try
        {
            // Converts or checks the file for its document type; a file of the
            // wrong kind never reaches the printer.
            var data = await type.BuildPrintDataAsync(_filePath);
            await UsbDirectPrinter.SendAsync(printer, data);
            SetStatus($"Enviado a «{printer.Name}»: {fileName}, {DateTime.Now:HH:mm}.", Color.ForestGreen);
        }
        catch (Exception ex)
        {
            SetStatus(Describe(ex, "No se pudo imprimir"), Color.Firebrick);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    private static string Describe(Exception ex, string prefix) =>
        ex is WrongFileException ? ex.Message : $"{prefix}: {ex.Message}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher.Dispose();
            _refreshTimer.Dispose();
            _pollTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
