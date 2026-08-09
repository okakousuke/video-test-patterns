using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RawManifestViewer;

public sealed class MainForm : Form
{
    private readonly Button _openFolderButton = new() { Text = "フォルダを開く", AutoSize = true };
    private readonly Label _folderLabel = new() { Text = "フォルダ未選択", AutoEllipsis = true, Dock = DockStyle.Fill };
    private readonly TreeView _manifestTree = new() { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true };
    private readonly ListBox _manifestList = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly ComboBox _colorModelFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox _sizeFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly Button _outputFolderButton = new() { Text = "出力先...", AutoSize = true };
    private readonly Label _outputFolderLabel = new() { Text = "出力先: 未指定", AutoEllipsis = true, Dock = DockStyle.Fill };
    private readonly PropertyGrid _propertyGrid = new() { HelpVisible = true, ToolbarVisible = false, Width = 760, HelpBackColor = Color.FromArgb(248, 250, 252) };
    private Panel? _propertyViewport;
    private readonly Label _previewTitle = new()
    {
        Text = "RAWファイルを選択してください",
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(0, 92, 180),
        Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
    };
    private readonly NumericUpDown _previewScale = new() { Minimum = 10, Maximum = 400, Value = 100, Increment = 25, Width = 72 };
    private readonly Button _fitPreviewButton = new() { Text = "全体表示", AutoSize = true };
    private readonly List<Button> _saveFormatButtons = [];
    private readonly PreviewCanvasPanel _previewPanel = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly PreviewPictureBox _preview = new() { BackColor = Color.Black, SizeMode = PictureBoxSizeMode.StretchImage };
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "フォルダを選択してください。", Spring = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly Button _savePngButton = new() { Text = "圧縮画像として保存...", AutoSize = true, Enabled = false };
    private readonly Button _saveRawButton = new() { Text = "RAWを出力先へコピー", AutoSize = true, Enabled = false };

    private readonly List<ManifestEntry> _entries = [];
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 10000, InitialDelay = 700, ReshowDelay = 200, ShowAlways = true };
    private Bitmap? _currentBitmap;
    private string? _currentManifestPath;
    private string? _currentRawPath;
    private string? _outputFolder;

    public MainForm()
    {
        Text = "RAW Manifest Viewer - 最小版";
        Width = 1380;
        Height = 760;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildUi();
        _outputFolderButton.Text = "出力先...";
        _openFolderButton.Text = "📁 フォルダを開く";
        _savePngButton.Text = "🖼 圧縮画像として保存...";
        _saveRawButton.Text = "📄 RAWを出力先へコピー";
        _openFolderButton.Click += (_, _) => OpenFolder();
        _manifestTree.AfterSelect += (_, _) => LoadSelectedManifest();
        _colorModelFilter.Items.AddRange(["すべて", "RGB", "YUV / YCbCr"]);
        _colorModelFilter.SelectedIndex = 0;
        _sizeFilter.Items.Add("すべて");
        _sizeFilter.SelectedIndex = 0;
        _colorModelFilter.SelectedIndexChanged += (_, _) => RefreshManifestTree();
        _sizeFilter.SelectedIndexChanged += (_, _) => RefreshManifestTree();
        _outputFolderLabel.Cursor = Cursors.Hand;
        _outputFolderLabel.Click += (_, _) => OpenOutputFolder();
        _outputFolderButton.Click += (_, _) => SelectOutputFolder();
        _savePngButton.Click += (_, _) => SavePng();
        _saveRawButton.Click += (_, _) => SaveRawCopy();
        _previewScale.ValueChanged += (_, _) => OnPreviewScaleChanged();
        _fitPreviewButton.Click += (_, _) => FitPreview();
        KeyDown += MainFormKeyDown;
        Shown += (_, _) => RestoreLastFolder();
        FormClosed += (_, _) => ReplaceBitmap(null);
        ConfigureToolTips();
    }

    private void ConfigureToolTips()
    {
        _toolTip.SetToolTip(_openFolderButton, "manifestとRAWファイルを含むフォルダを開きます。");
        _toolTip.SetToolTip(_folderLabel, "現在読み込んでいるフォルダです。");
        _toolTip.SetToolTip(_outputFolderButton, "画像の保存先フォルダを変更します。");
        _toolTip.SetToolTip(_outputFolderLabel, "現在の保存先です。クリックするとエクスプローラーで開きます。");
        _toolTip.SetToolTip(_savePngButton, "現在のプレビューをPNG・JPEG・TIFF・BMP・GIFの圧縮画像として保存します。");
        _toolTip.SetToolTip(_saveRawButton, "選択中のRAWファイルを、指定した出力先フォルダへそのままコピーします。");
        _toolTip.SetToolTip(_manifestTree, "パターン名ごとにmanifestを分類しています。項目を選ぶとプレビューします。");
        _toolTip.SetToolTip(_colorModelFilter, "RGBまたはYUV / YCbCr系で一覧を絞り込みます。");
        _toolTip.SetToolTip(_sizeFilter, "画像サイズで一覧を絞り込みます。");
        _toolTip.SetToolTip(_propertyGrid, "manifestに記録された生成条件です。横スクロールで長い値を確認できます。");
        _toolTip.SetToolTip(_previewScale, "表示倍率です。50%以下は10%刻み、それより大きい値は25%刻みです。");
        _toolTip.SetToolTip(_fitPreviewButton, "画像全体が見える倍率へ戻し、中央に配置します。");
        _toolTip.SetToolTip(_previewPanel, "全体表示では中央配置、倍率を手動変更した場合は左上起点で表示します。");
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 2,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 470));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var folderBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, AutoSize = true };
        folderBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        folderBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderBar.Controls.Add(_openFolderButton, 0, 0);
        folderBar.Controls.Add(_folderLabel, 1, 0);
        folderBar.Controls.Add(_outputFolderButton, 2, 0);
        folderBar.Controls.Add(_outputFolderLabel, 3, 0);
        folderBar.Controls.Add(_savePngButton, 4, 0);
        root.Controls.Add(CreateTopBar(), 0, 0);
        root.SetColumnSpan(root.GetControlFromPosition(0, 0)!, 2);

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(0, 8, 8, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        left.Controls.Add(WrapGroup("manifest一覧", _manifestList), 0, 0);
        left.Controls.Add(WrapGroup("manifestパラメータ", _propertyGrid), 0, 1);
        left.Controls.Add(CreateManifestBrowser(), 0, 0);
        root.Controls.Add(CreateLeftPanel(), 0, 1);

        var previewGroup = WrapGroup("プレビュー（アスペクト比維持）", _preview);
        previewGroup.Padding = new Padding(4, 20, 4, 4);
        root.Controls.Add(CreatePreviewGroup(), 1, 1);

        var status = new StatusStrip { SizingGrip = false };
        status.Items.Add(_statusLabel);
        root.Controls.Add(status, 0, 2);
        root.SetColumnSpan(status, 2);

        Controls.Add(root);
    }

    private static GroupBox WrapGroup(string title, Control content) => new()
    {
        Text = title,
        Dock = DockStyle.Fill,
        Padding = new Padding(8),
        Controls = { content },
    };

    private Control CreateTopBar()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 2, AutoSize = true };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 390));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bar.Controls.Add(_openFolderButton, 0, 0);
        bar.Controls.Add(_folderLabel, 1, 0);
        bar.Controls.Add(_outputFolderButton, 2, 0);
        bar.Controls.Add(_outputFolderLabel, 2, 1);
        bar.SetColumnSpan(_outputFolderLabel, 2);
        bar.Controls.Add(_savePngButton, 4, 0);
        bar.Controls.Add(_saveRawButton, 5, 0);
        return bar;
    }

    private Control CreateLeftPanel()
    {
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(0, 8, 8, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 43));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 57));
        left.Controls.Add(CreateManifestBrowser(), 0, 0);
        left.Controls.Add(CreatePropertyBrowser(), 0, 1);
        return left;
    }

    private Control CreatePropertyBrowser()
    {
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _propertyViewport = viewport;
        _propertyGrid.Location = Point.Empty;
        _propertyGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _propertyGrid.Height = 260;
        viewport.Resize += (_, _) =>
        {
            _propertyGrid.Height = Math.Max(1, viewport.ClientSize.Height);
            AdjustPropertyGridColumns();
        };
        viewport.Controls.Add(_propertyGrid);
        return WrapGroup("⚙ manifestパラメータ  [?]", viewport);
    }

    private GroupBox CreateManifestBrowser()
    {
        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(0, 2, 0, 0), WrapContents = false };
        filters.Controls.Add(new Label { Text = "色モデル", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        filters.Controls.Add(_colorModelFilter);
        filters.Controls.Add(new Label { Text = "サイズ", AutoSize = true, Padding = new Padding(10, 6, 4, 0) });
        filters.Controls.Add(_sizeFilter);

        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(_manifestTree);
        content.Controls.Add(filters);
        return WrapGroup("☷ manifest一覧（パターン別）", content);
    }

    private GroupBox CreatePreviewGroup()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 30, ColumnCount = 9 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // 主要パラメータは最下部のステータスバーへ集約する。
        _previewTitle.Visible = false;
        header.Controls.Add(new Label { Text = "表示倍率", AutoSize = true, Anchor = AnchorStyles.Right, Padding = new Padding(0, 5, 6, 0) }, 0, 0);
        header.Controls.Add(_previewScale, 1, 0);
        header.Controls.Add(_fitPreviewButton, 2, 0);
        header.Controls.Add(new Label { Text = "保存", AutoSize = true, Anchor = AnchorStyles.Right, Padding = new Padding(8, 5, 4, 0) }, 3, 0);
        header.Controls.Add(CreateSaveButton("PNG", ImageFormat.Png, "png"), 4, 0);
        header.Controls.Add(CreateSaveButton("JPG", ImageFormat.Jpeg, "jpg"), 5, 0);
        header.Controls.Add(CreateSaveButton("TIFF", ImageFormat.Tiff, "tiff"), 6, 0);
        header.Controls.Add(CreateSaveButton("BMP", ImageFormat.Bmp, "bmp"), 7, 0);
        header.Controls.Add(CreateSaveButton("GIF", ImageFormat.Gif, "gif"), 8, 0);

        _previewPanel.Controls.Add(_preview);
        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(_previewPanel);
        content.Controls.Add(header);
        return WrapGroup("プレビュー（アスペクト比維持）", content);
    }

    private Button CreateSaveButton(string label, ImageFormat format, string extension)
    {
        var shortcut = extension switch
        {
            "png" => "Ctrl+1",
            "jpg" => "Ctrl+2",
            "tiff" => "Ctrl+3",
            "bmp" => "Ctrl+4",
            "gif" => "Ctrl+5",
            _ => "",
        };
        var button = new Button { Text = $"{label} ({shortcut})", AutoSize = true, Enabled = false };
        button.Click += (_, _) => SaveAs(format, extension);
        _toolTip.SetToolTip(button, $"プレビューを{label}形式で保存します。ショートカット: {shortcut}");
        _saveFormatButtons.Add(button);
        return button;
    }

    private void SelectOutputFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "保存先フォルダを選択してください。",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _outputFolder = dialog.SelectedPath;
        _outputFolderLabel.Text = "出力先: " + _outputFolder;
        _outputFolderLabel.ForeColor = Color.FromArgb(0, 92, 180);
    }

    private void SetOutputFolder(string folder)
    {
        _outputFolder = folder;
        _outputFolderLabel.Text = "出力先: " + folder;
        _outputFolderLabel.ForeColor = Color.FromArgb(0, 92, 180);
    }

    private void OpenOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(_outputFolder) || !Directory.Exists(_outputFolder)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _outputFolder,
            UseShellExecute = true,
        });
    }

    private void OpenFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "manifestとRAWファイルが入ったフォルダを選択してください。",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _folderLabel.Text = dialog.SelectedPath;
        SetOutputFolder(dialog.SelectedPath);
        RememberLastFolder(dialog.SelectedPath);
        ScanManifests(dialog.SelectedPath);
        RefreshSizeFilter();
        RefreshManifestTree();
    }

    private static string LastFolderFilePath => Path.Combine(Application.UserAppDataPath, "last-folder.txt");

    private void RestoreLastFolder()
    {
        try
        {
            if (!File.Exists(LastFolderFilePath)) return;
            var folder = File.ReadAllText(LastFolderFilePath).Trim();
            if (!Directory.Exists(folder)) return;
            _folderLabel.Text = folder;
            SetOutputFolder(folder);
            ScanManifests(folder);
            RefreshSizeFilter();
            RefreshManifestTree();
        }
        catch (IOException)
        {
            // 前回フォルダの復元に失敗しても、手動選択は可能にしておく。
        }
    }

    private static void RememberLastFolder(string folder)
    {
        Directory.CreateDirectory(Application.UserAppDataPath);
        File.WriteAllText(LastFolderFilePath, folder);
    }

    private void MainFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Control) return;
        var handled = e.KeyCode switch
        {
            Keys.D1 or Keys.NumPad1 => SaveShortcut(ImageFormat.Png, "png"),
            Keys.D2 or Keys.NumPad2 => SaveShortcut(ImageFormat.Jpeg, "jpg"),
            Keys.D3 or Keys.NumPad3 => SaveShortcut(ImageFormat.Tiff, "tiff"),
            Keys.D4 or Keys.NumPad4 => SaveShortcut(ImageFormat.Bmp, "bmp"),
            Keys.D5 or Keys.NumPad5 => SaveShortcut(ImageFormat.Gif, "gif"),
            _ => false,
        };
        if (handled) e.SuppressKeyPress = true;
    }

    private bool SaveShortcut(ImageFormat format, string extension)
    {
        if (_currentBitmap is null) return false;
        SaveAs(format, extension);
        return true;
    }

    private void ScanManifests(string folder)
    {
        _entries.Clear();
        _manifestTree.Nodes.Clear();
        ReplaceBitmap(null);
        _propertyGrid.SelectedObject = null;
        _savePngButton.Enabled = false;
        _saveRawButton.Enabled = false;
        _currentRawPath = null;
        _previewTitle.Text = "RAWファイルを選択してください";

        foreach (var path in Directory.EnumerateFiles(folder, "*.manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = ManifestInfo.Load(path);
                _entries.Add(new ManifestEntry(path, manifest));
            }
            catch (Exception ex)
            {
                _entries.Add(new ManifestEntry(path, null, ex.Message));
            }
        }

        _statusLabel.Text = $"manifest {_entries.Count}件。対応形式を選択してください。";
    }

    private void RefreshSizeFilter()
    {
        var current = _sizeFilter.SelectedItem?.ToString() ?? "すべて";
        var sizes = _entries
            .Where(e => e.Manifest is not null)
            .Select(e => $"{e.Manifest!.Width} x {e.Manifest.Height}")
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        _sizeFilter.BeginUpdate();
        _sizeFilter.Items.Clear();
        _sizeFilter.Items.Add("すべて");
        _sizeFilter.Items.AddRange(sizes);
        _sizeFilter.SelectedItem = _sizeFilter.Items.Contains(current) ? current : "すべて";
        _sizeFilter.EndUpdate();
    }

    private void RefreshManifestTree()
    {
        var colorFilter = _colorModelFilter.SelectedItem?.ToString() ?? "すべて";
        var sizeFilter = _sizeFilter.SelectedItem?.ToString() ?? "すべて";
        var roots = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

        _manifestTree.BeginUpdate();
        _manifestTree.Nodes.Clear();
        foreach (var entry in _entries.Where(e => MatchesFilter(e, colorFilter, sizeFilter)))
        {
            var group = entry.Manifest?.Id ?? "読み込みエラー";
            if (!roots.TryGetValue(group, out var root))
            {
                root = new TreeNode(group);
                roots.Add(group, root);
                _manifestTree.Nodes.Add(root);
            }

            var node = new TreeNode(ManifestEntryText(entry)) { Tag = entry, ToolTipText = entry.Error ?? entry.Path };
            root.Nodes.Add(node);
        }
        foreach (TreeNode root in _manifestTree.Nodes) root.Expand();
        _manifestTree.EndUpdate();
    }

    private static bool MatchesFilter(ManifestEntry entry, string colorFilter, string sizeFilter)
    {
        if (entry.Manifest is null) return colorFilter == "すべて" && sizeFilter == "すべて";
        var manifest = entry.Manifest;
        var colorMatches = colorFilter == "すべて"
            || (colorFilter == "YUV / YCbCr" && string.Equals(manifest.ColorModel, "ycbcr", StringComparison.OrdinalIgnoreCase))
            || string.Equals(manifest.ColorModel, colorFilter, StringComparison.OrdinalIgnoreCase);
        var sizeMatches = sizeFilter == "すべて" || sizeFilter == $"{manifest.Width} x {manifest.Height}";
        return colorMatches && sizeMatches;
    }

    private static string ManifestEntryText(ManifestEntry entry)
    {
        if (entry.Manifest is null) return Path.GetFileName(entry.Path);
        var manifest = entry.Manifest;
        return $"{Path.GetFileName(manifest.Raw.Path)}  [{manifest.ColorModel}, {manifest.Storage}, {manifest.BitDepth}bit, {manifest.Width}x{manifest.Height}]";
    }

    private void LoadSelectedManifest()
    {
        if (_manifestTree.SelectedNode?.Tag is not ManifestEntry entry)
            return;
        _currentManifestPath = entry.Path;

        if (entry.Manifest is null)
        {
            ReplaceBitmap(null);
            _propertyGrid.SelectedObject = null;
            _savePngButton.Enabled = false;
            _saveRawButton.Enabled = false;
            _currentRawPath = null;
            _previewTitle.Text = Path.GetFileName(entry.Path);
            _statusLabel.Text = $"読み込み不可: {entry.Error}";
            return;
        }

        var manifest = entry.Manifest;
        _propertyGrid.SelectedObject = ToDisplay(manifest, entry.Path);
        BeginInvoke((Action)AdjustPropertyGridColumns);
        _previewTitle.Text = FormatPrimaryParameters(manifest);

        if (!manifest.SupportsPreview)
        {
            ReplaceBitmap(null);
            _savePngButton.Enabled = false;
            _saveRawButton.Enabled = false;
            _currentRawPath = null;
            _statusLabel.Text = $"読み込み済み（プレビュー未対応）: {manifest.ColorModel}, {manifest.BitDepth}bit, {manifest.Storage}";
            return;
        }

        try
        {
            var rawPath = manifest.ResolveRawPath(entry.Path);
            var bitmap = LoadPreview(rawPath, manifest);
            ReplaceBitmap(bitmap);
            _savePngButton.Enabled = true;
            _saveRawButton.Enabled = true;
            _currentRawPath = rawPath;
            _statusLabel.Text = FormatPrimaryParameters(manifest);
        }
        catch (Exception ex)
        {
            ReplaceBitmap(null);
            _savePngButton.Enabled = false;
            _saveRawButton.Enabled = false;
            _currentRawPath = null;
            _statusLabel.Text = $"RAW読み込みエラー: {ex.Message}";
        }
    }

    private static ManifestDisplay ToDisplay(ManifestInfo manifest, string path) => new()
    {
        Id = manifest.Id ?? Path.GetFileNameWithoutExtension(path),
        RawFile = manifest.Raw.Path,
        Size = $"{manifest.Width} x {manifest.Height}",
        ColorModel = manifest.ColorModel ?? "",
        ChannelOrder = ValueOrNote(manifest.ChannelOrder, "この格納形式では未使用"),
        Subsampling = manifest.Subsampling ?? "",
        BitDepth = manifest.BitDepth.ToString(),
        Range = ValueOrNote(manifest.Range, "この色モデルでは未使用"),
        Matrix = ValueOrNote(manifest.Matrix, "この色モデルでは未使用"),
        Storage = manifest.Storage ?? "",
        Alignment = ValueOrNote(manifest.Alignment, "この格納形式では未使用"),
        RawBytes = manifest.RawBytes.ToString(),
        Sha256 = manifest.Raw.Sha256 ?? "未指定",
    };

    private static string ValueOrNote(string? value, string note) =>
        string.IsNullOrWhiteSpace(value) ? $"（{note}）" : value;

    private static string FormatPrimaryParameters(ManifestInfo manifest) =>
        $"色モデル: {manifest.ColorModel} / 色差サブサンプリング: {manifest.Subsampling} / ビット深度: {manifest.BitDepth}bit / 格納形式: {manifest.Storage} / 画像サイズ: {manifest.Width} x {manifest.Height}";

    private static Bitmap LoadPreview(string rawPath, ManifestInfo manifest)
    {
        if (!File.Exists(rawPath))
            throw new FileNotFoundException("manifestが指すRAWファイルがありません。", rawPath);

        var isYcbcr = string.Equals(manifest.ColorModel, "ycbcr", StringComparison.OrdinalIgnoreCase);
        var isPlanar = string.Equals(manifest.Storage, "planar", StringComparison.OrdinalIgnoreCase);
        var isNv12 = string.Equals(manifest.Storage, "nv12", StringComparison.OrdinalIgnoreCase);
        var isP010 = string.Equals(manifest.Storage, "p010", StringComparison.OrdinalIgnoreCase);
        var isV210 = string.Equals(manifest.Storage, "v210", StringComparison.OrdinalIgnoreCase);
        var isMipi10 = string.Equals(manifest.Storage, "mipi10", StringComparison.OrdinalIgnoreCase);
        var bytesPerSample = manifest.BitDepth == 10 ? 2 : 1;
        var is422 = string.Equals(manifest.Subsampling, "4:2:2", StringComparison.OrdinalIgnoreCase);
        var expectedMinimum = isV210
            ? V210RowStride(manifest.Width) * manifest.Height
            : isMipi10
                ? Mipi10ExpectedBytes(manifest)
            : isNv12 || isP010
            ? checked(manifest.Width * manifest.Height * 3 / 2 * bytesPerSample)
            : isYcbcr && is422
                ? checked(manifest.Width * manifest.Height * 2)
                : checked(manifest.Width * manifest.Height * 3 * bytesPerSample);
        var fileLength = new FileInfo(rawPath).Length;
        if (fileLength < expectedMinimum)
            throw new InvalidDataException($"RAWサイズが不足しています: {fileLength} < {expectedMinimum} bytes");

        var bitmap = new Bitmap(manifest.Width, manifest.Height, PixelFormat.Format24bppRgb);
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

        try
        {
            var data = File.ReadAllBytes(rawPath);
            var bgr = string.Equals(manifest.ChannelOrder, "BGR", StringComparison.OrdinalIgnoreCase);

            for (var y = 0; y < manifest.Height; y++)
            {
                var destination = bitmapData.Scan0 + y * bitmapData.Stride;
                for (var x = 0; x < manifest.Width; x++)
                {
                    var pixel = y * manifest.Width + x;
                    var target = destination + x * 3;
                    int first, second, third;
                    if (isV210)
                    {
                        (first, second, third) = ReadV210(data, manifest.Width, x, y);
                    }
                    else if (isMipi10)
                    {
                        (first, second, third) = ReadMipi10(data, manifest, x, y);
                    }
                    else if (isNv12 || isP010)
                    {
                        var ySize = manifest.Width * manifest.Height;
                        var chroma = ySize * bytesPerSample + (y / 2) * manifest.Width * bytesPerSample + (x / 2) * 2 * bytesPerSample;
                        first = ReadCode(data, pixel * bytesPerSample, manifest.BitDepth, isP010 ? "msb" : manifest.Alignment);
                        second = ReadCode(data, chroma, manifest.BitDepth, isP010 ? "msb" : manifest.Alignment);
                        third = ReadCode(data, chroma + bytesPerSample, manifest.BitDepth, isP010 ? "msb" : manifest.Alignment);
                    }
                    else if (isYcbcr && is422)
                    {
                        var source = (y * manifest.Width + x / 2 * 2) * 2;
                        first = data[source + (x % 2 == 0 ? 1 : 3)];
                        second = data[source];
                        third = data[source + 2];
                    }
                    else
                    {
                        var source = isPlanar ? pixel * bytesPerSample : pixel * 3;
                        var plane = manifest.Width * manifest.Height * bytesPerSample;
                        first = isPlanar
                            ? ReadCode(data, source, manifest.BitDepth, manifest.Alignment)
                            : data[source + (bgr ? 2 : 0)];
                        second = isPlanar
                            ? ReadCode(data, source + plane, manifest.BitDepth, manifest.Alignment)
                            : data[source + 1];
                        third = isPlanar
                            ? ReadCode(data, source + 2 * plane, manifest.BitDepth, manifest.Alignment)
                            : data[source + (bgr ? 0 : 2)];
                    }
                    var (r, g, b) = isYcbcr
                        ? YcbcrToRgb(first, second, third, manifest.Matrix, manifest.Range, manifest.BitDepth)
                        : (ToByte(first / ((1 << manifest.BitDepth) - 1.0)), ToByte(second / ((1 << manifest.BitDepth) - 1.0)), ToByte(third / ((1 << manifest.BitDepth) - 1.0)));
                    Marshal.WriteByte(target, b);
                    Marshal.WriteByte(target + 1, g);
                    Marshal.WriteByte(target + 2, r);
                }
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        return bitmap;
    }

    private static int ReadCode(byte[] data, int offset, int bitDepth, string? alignment)
    {
        if (bitDepth == 8) return data[offset];
        var container = data[offset] | (data[offset + 1] << 8);
        return string.Equals(alignment, "msb", StringComparison.OrdinalIgnoreCase)
            ? container >> 6
            : container & 0x03ff;
    }

    private static int V210RowStride(int width) => ((width / 6 * 16 + 127) / 128) * 128;

    private static (int Y, int Cb, int Cr) ReadV210(byte[] data, int width, int x, int y)
    {
        var group = x / 6;
        var offset = y * V210RowStride(width) + group * 16;
        var w0 = ReadUInt32LittleEndian(data, offset);
        var w1 = ReadUInt32LittleEndian(data, offset + 4);
        var w2 = ReadUInt32LittleEndian(data, offset + 8);
        var w3 = ReadUInt32LittleEndian(data, offset + 12);
        var i = x % 6;
        return i switch
        {
            0 => (Field(w0, 1), Field(w0, 0), Field(w0, 2)),
            1 => (Field(w1, 0), Field(w0, 0), Field(w0, 2)),
            2 => (Field(w1, 2), Field(w1, 1), Field(w2, 0)),
            3 => (Field(w2, 1), Field(w1, 1), Field(w2, 0)),
            4 => (Field(w3, 0), Field(w2, 2), Field(w3, 1)),
            _ => (Field(w3, 2), Field(w2, 2), Field(w3, 1)),
        };
    }

    private static int Mipi10ExpectedBytes(ManifestInfo manifest)
    {
        var y = Mipi10PlaneBytes(manifest.Width, manifest.Height);
        if (string.Equals(manifest.ColorModel, "rgb", StringComparison.OrdinalIgnoreCase)) return y * 3;
        var cw = string.Equals(manifest.Subsampling, "4:4:4", StringComparison.OrdinalIgnoreCase) ? manifest.Width : manifest.Width / 2;
        var ch = string.Equals(manifest.Subsampling, "4:2:0", StringComparison.OrdinalIgnoreCase) ? manifest.Height / 2 : manifest.Height;
        return y + Mipi10PlaneBytes(cw, ch) * 2;
    }

    private static (int First, int Second, int Third) ReadMipi10(byte[] data, ManifestInfo manifest, int x, int y)
    {
        var yBytes = Mipi10PlaneBytes(manifest.Width, manifest.Height);
        if (string.Equals(manifest.ColorModel, "rgb", StringComparison.OrdinalIgnoreCase))
            return (ReadMipi10Sample(data, 0, manifest.Width, x, y),
                ReadMipi10Sample(data, yBytes, manifest.Width, x, y),
                ReadMipi10Sample(data, yBytes * 2, manifest.Width, x, y));

        var cw = string.Equals(manifest.Subsampling, "4:4:4", StringComparison.OrdinalIgnoreCase) ? manifest.Width : manifest.Width / 2;
        var ch = string.Equals(manifest.Subsampling, "4:2:0", StringComparison.OrdinalIgnoreCase) ? manifest.Height / 2 : manifest.Height;
        var cBytes = Mipi10PlaneBytes(cw, ch);
        var cx = cw == manifest.Width ? x : x / 2;
        var cy = ch == manifest.Height ? y : y / 2;
        return (ReadMipi10Sample(data, 0, manifest.Width, x, y),
            ReadMipi10Sample(data, yBytes, cw, cx, cy),
            ReadMipi10Sample(data, yBytes + cBytes, cw, cx, cy));
    }

    private static int Mipi10PlaneBytes(int width, int height) => checked(height * (width / 4) * 5);

    private static int ReadMipi10Sample(byte[] data, int planeOffset, int planeWidth, int x, int y)
    {
        var group = y * (planeWidth / 4) + x / 4;
        var offset = planeOffset + group * 5;
        var index = x % 4;
        return (data[offset + index] << 2) | ((data[offset + 4] >> (index * 2)) & 0x03);
    }

    private static uint ReadUInt32LittleEndian(byte[] data, int offset) =>
        (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private static int Field(uint word, int position) => (int)((word >> (position * 10)) & 0x03ff);

    private static (byte R, byte G, byte B) YcbcrToRgb(int yCode, int cbCode, int crCode, string? matrix, string? range, int bitDepth)
    {
        var (kr, kb) = matrix?.ToLowerInvariant() switch
        {
            "bt601" => (0.299, 0.114),
            "bt2020" => (0.2627, 0.0593),
            _ => (0.2126, 0.0722), // bt709 is the default for an omitted value.
        };
        var kg = 1.0 - kr - kb;
        var limited = string.Equals(range, "limited", StringComparison.OrdinalIgnoreCase);
        var shift = 1 << (bitDepth - 8);
        var peak = (1 << bitDepth) - 1.0;
        var y = limited ? (yCode - 16.0 * shift) / (219.0 * shift) : yCode / peak;
        var cb = limited ? (cbCode - 128.0 * shift) / (224.0 * shift) : (cbCode - 128.0 * shift) / peak;
        var cr = limited ? (crCode - 128.0 * shift) / (224.0 * shift) : (crCode - 128.0 * shift) / peak;
        var r = y + 2.0 * (1.0 - kr) * cr;
        var b = y + 2.0 * (1.0 - kb) * cb;
        var g = (y - kr * r - kb * b) / kg;
        return (ToByte(r), ToByte(g), ToByte(b));
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255.0), 0, 255);

    private void SavePng()
    {
        if (_currentBitmap is null)
            return;

        using var dialog = new SaveFileDialog
        {
            Filter = "PNG画像 (*.png)|*.png",
            FileName = Path.GetFileNameWithoutExtension(_currentManifestPath ?? "preview") + ".png",
        };
        dialog.Filter = "PNG (*.png)|*.png|JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg|TIFF (*.tif;*.tiff)|*.tif;*.tiff|BMP (*.bmp)|*.bmp|GIF (*.gif)|*.gif";
        dialog.FilterIndex = 1;
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _currentBitmap.Save(dialog.FileName, ImageFormatFor(dialog.FilterIndex));
            _statusLabel.Text = $"保存しました: {dialog.FileName}";
        }
    }

    private void SaveRawCopy()
    {
        if (string.IsNullOrWhiteSpace(_currentRawPath) || !File.Exists(_currentRawPath))
        {
            _statusLabel.Text = "RAWファイルを選択してください。";
            return;
        }

        if (string.IsNullOrWhiteSpace(_outputFolder) || !Directory.Exists(_outputFolder))
        {
            _statusLabel.Text = "出力先フォルダを指定してください。";
            return;
        }

        var destination = Path.Combine(_outputFolder, Path.GetFileName(_currentRawPath));
        if (string.Equals(Path.GetFullPath(_currentRawPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            _statusLabel.Text = "出力先がRAWファイルと同じです。別の出力先を指定してください。";
            return;
        }

        if (File.Exists(destination)
            && MessageBox.Show(this, $"同名のRAWファイルを上書きしますか？\n{destination}", "上書き確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        try
        {
            File.Copy(_currentRawPath, destination, overwrite: true);
            _statusLabel.Text = $"RAWコピー完了: {Path.GetFileName(destination)}";
        }
        catch (IOException ex)
        {
            _statusLabel.Text = $"RAWコピーエラー: {ex.Message}";
        }
    }

    private static ImageFormat ImageFormatFor(int filterIndex) => filterIndex switch
    {
        2 => ImageFormat.Jpeg,
        3 => ImageFormat.Tiff,
        4 => ImageFormat.Bmp,
        5 => ImageFormat.Gif,
        _ => ImageFormat.Png,
    };

    private void SaveAs(ImageFormat format, string extension)
    {
        if (_currentBitmap is null) return;

        var fileName = Path.GetFileNameWithoutExtension(_previewTitle.Text) + "." + extension;
        if (!string.IsNullOrWhiteSpace(_outputFolder))
        {
            var outputPath = Path.Combine(_outputFolder, fileName);
            if (File.Exists(outputPath)
                && MessageBox.Show(this, $"同名ファイルを上書きしますか？\n{outputPath}", "上書き確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            _currentBitmap.Save(outputPath, format);
            _statusLabel.Text = $"保存しました: {outputPath}";
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = $"{extension.ToUpperInvariant()} (*.{extension})|*.{extension}",
            FileName = fileName,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _currentBitmap.Save(dialog.FileName, format);
        _statusLabel.Text = $"保存しました: {dialog.FileName}";
    }

    private void AdjustPropertyGridColumns()
    {
        if (_propertyGrid.SelectedObject is null) return;

        var widest = System.ComponentModel.TypeDescriptor.GetProperties(_propertyGrid.SelectedObject)
            .Cast<System.ComponentModel.PropertyDescriptor>()
            .Select(property => TextRenderer.MeasureText(property.DisplayName, _propertyGrid.Font).Width)
            .DefaultIfEmpty(150)
            .Max();
        // 一番長い表示名まで左列を広げ、通常の値は横スクロールせず見える幅を確保する。
        var target = Math.Min(155, widest + 14);
        var viewportWidth = _propertyViewport?.ClientSize.Width ?? 0;
        _propertyGrid.Width = Math.Max(viewportWidth, target + 300);

        var gridViewField = typeof(PropertyGrid).GetField("gridView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var gridView = gridViewField?.GetValue(_propertyGrid);
        var moveSplitter = gridView?.GetType().GetMethod("MoveSplitterTo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        moveSplitter?.Invoke(gridView, [target]);
    }

    private void OnPreviewScaleChanged()
    {
        _previewScale.Increment = _previewScale.Value < 20 ? 10 : 25;
        UpdatePreviewSize();
    }

    private void UpdatePreviewSize()
    {
        if (_currentBitmap is null)
        {
            _preview.Size = Size.Empty;
            return;
        }

        var scale = (float)_previewScale.Value / 100f;
        _preview.Size = new Size(
            Math.Max(1, (int)Math.Round(_currentBitmap.Width * scale)),
            Math.Max(1, (int)Math.Round(_currentBitmap.Height * scale)));
        _preview.Location = Point.Empty;
    }

    private void FitPreview()
    {
        if (_currentBitmap is null) return;

        var availableWidth = Math.Max(1, _previewPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
        var availableHeight = Math.Max(1, _previewPanel.ClientSize.Height - SystemInformation.HorizontalScrollBarHeight - 8);
        var scale = Math.Min(availableWidth / (float)_currentBitmap.Width, availableHeight / (float)_currentBitmap.Height);
        var percentage = Math.Clamp((decimal)(scale * 100f), _previewScale.Minimum, _previewScale.Maximum);
        _previewScale.Value = decimal.Round(percentage, 0);
        _previewPanel.AutoScrollPosition = Point.Empty;
        CenterPreview();
    }

    private void CenterPreview()
    {
        var x = Math.Max(0, (_previewPanel.ClientSize.Width - _preview.Width) / 2);
        var y = Math.Max(0, (_previewPanel.ClientSize.Height - _preview.Height) / 2);
        _preview.Location = new Point(x, y);
    }

    private void ReplaceBitmap(Bitmap? bitmap)
    {
        var old = _currentBitmap;
        _currentBitmap = bitmap;
        _preview.Image = bitmap;
        foreach (var button in _saveFormatButtons) button.Enabled = bitmap is not null;
        if (bitmap is null) UpdatePreviewSize();
        else BeginInvoke((Action)FitPreview);
        old?.Dispose();
    }

    private sealed record ManifestEntry(string Path, ManifestInfo? Manifest, string? Error = null);

    private sealed class ManifestListItem
    {
        private readonly string _path;
        private readonly ManifestInfo? _manifest;
        private readonly string? _error;

        public ManifestListItem(string path, ManifestInfo? manifest, string? error = null)
        {
            _path = path;
            _manifest = manifest;
            _error = error;
        }

        public override string ToString()
        {
            if (_manifest is null)
                return $"[エラー] {Path.GetFileName(_path)}";

            var rawName = _manifest.Raw.Path;
            return $"{Path.GetFileName(rawName)}  [{_manifest.Storage}, {_manifest.BitDepth}bit, {_manifest.Width}x{_manifest.Height}]";
        }
    }

    private sealed class PreviewCanvasPanel : Panel
    {
        private static readonly Color TileA = Color.FromArgb(47, 50, 57);
        private static readonly Color TileB = Color.FromArgb(54, 50, 61);
        private const int TileSize = 16;

        public PreviewCanvasPanel()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            for (var y = 0; y < Height; y += TileSize)
            for (var x = 0; x < Width; x += TileSize)
            {
                var parity = (x / TileSize + y / TileSize) % 2;
                using var brush = new SolidBrush(parity == 0 ? TileA : TileB);
                e.Graphics.FillRectangle(brush, x, y, TileSize, TileSize);
            }
        }
    }

    private sealed class PreviewPictureBox : PictureBox
    {
        private static readonly Color BorderColor = Color.FromArgb(0, 180, 180);

        protected override void OnPaint(PaintEventArgs pe)
        {
            base.OnPaint(pe);
            if (Width < 2 || Height < 2) return;
            using var pen = new Pen(BorderColor, 1);
            pe.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
