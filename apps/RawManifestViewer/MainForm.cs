using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RawManifestViewer;

public sealed class MainForm : Form
{
    private readonly Button _openFolderButton = new() { Text = "フォルダを開く", AutoSize = true };
    private readonly Label _folderLabel = new() { Text = "フォルダ未選択", AutoEllipsis = true, Dock = DockStyle.Fill };
    private readonly ListBox _manifestList = new() { Dock = DockStyle.Fill };
    private readonly PropertyGrid _propertyGrid = new() { Dock = DockStyle.Fill, HelpVisible = false, ToolbarVisible = false };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(35, 35, 35), SizeMode = PictureBoxSizeMode.Zoom };
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "フォルダを選択してください。", Spring = true };
    private readonly Button _savePngButton = new() { Text = "PNG保存", AutoSize = true, Enabled = false };

    private readonly List<ManifestEntry> _entries = [];
    private Bitmap? _currentBitmap;
    private string? _currentManifestPath;

    public MainForm()
    {
        Text = "RAW Manifest Viewer - 最小版";
        Width = 1200;
        Height = 760;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        _openFolderButton.Click += (_, _) => OpenFolder();
        _manifestList.SelectedIndexChanged += (_, _) => LoadSelectedManifest();
        _savePngButton.Click += (_, _) => SavePng();
        FormClosed += (_, _) => ReplaceBitmap(null);
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
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var folderBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        folderBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderBar.Controls.Add(_openFolderButton, 0, 0);
        folderBar.Controls.Add(_folderLabel, 1, 0);
        folderBar.Controls.Add(_savePngButton, 2, 0);
        root.Controls.Add(folderBar, 0, 0);
        root.SetColumnSpan(folderBar, 2);

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(0, 8, 8, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        left.Controls.Add(WrapGroup("manifest一覧", _manifestList), 0, 0);
        left.Controls.Add(WrapGroup("manifestパラメータ", _propertyGrid), 0, 1);
        root.Controls.Add(left, 0, 1);

        var previewGroup = WrapGroup("プレビュー（アスペクト比維持）", _preview);
        previewGroup.Padding = new Padding(4, 20, 4, 4);
        root.Controls.Add(previewGroup, 1, 1);

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
        ScanManifests(dialog.SelectedPath);
    }

    private void ScanManifests(string folder)
    {
        _entries.Clear();
        _manifestList.Items.Clear();
        ReplaceBitmap(null);
        _propertyGrid.SelectedObject = null;
        _savePngButton.Enabled = false;

        foreach (var path in Directory.EnumerateFiles(folder, "*.manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = ManifestInfo.Load(path);
                _entries.Add(new ManifestEntry(path, manifest));
                _manifestList.Items.Add(new ManifestListItem(path, manifest));
            }
            catch (Exception ex)
            {
                _entries.Add(new ManifestEntry(path, null, ex.Message));
                _manifestList.Items.Add(new ManifestListItem(path, null, ex.Message));
            }
        }

        _statusLabel.Text = $"manifest {_entries.Count}件。対応形式を選択してください。";
    }

    private void LoadSelectedManifest()
    {
        if (_manifestList.SelectedIndex < 0 || _manifestList.SelectedIndex >= _entries.Count)
            return;

        var entry = _entries[_manifestList.SelectedIndex];
        _currentManifestPath = entry.Path;

        if (entry.Manifest is null)
        {
            ReplaceBitmap(null);
            _propertyGrid.SelectedObject = null;
            _savePngButton.Enabled = false;
            _statusLabel.Text = $"読み込み不可: {entry.Error}";
            return;
        }

        var manifest = entry.Manifest;
        _propertyGrid.SelectedObject = ToDisplay(manifest, entry.Path);

        if (!manifest.SupportsRgb8Preview)
        {
            ReplaceBitmap(null);
            _savePngButton.Enabled = false;
            _statusLabel.Text = $"読み込み済み（プレビュー未対応）: {manifest.ColorModel}, {manifest.BitDepth}bit, {manifest.Storage}";
            return;
        }

        try
        {
            var rawPath = manifest.ResolveRawPath(entry.Path);
            var bitmap = LoadRgb8(rawPath, manifest);
            ReplaceBitmap(bitmap);
            _savePngButton.Enabled = true;
            _statusLabel.Text = $"表示中: {Path.GetFileName(rawPath)} ({bitmap.Width}x{bitmap.Height})";
        }
        catch (Exception ex)
        {
            ReplaceBitmap(null);
            _savePngButton.Enabled = false;
            _statusLabel.Text = $"RAW読み込みエラー: {ex.Message}";
        }
    }

    private static ManifestDisplay ToDisplay(ManifestInfo manifest, string path) => new()
    {
        Id = manifest.Id ?? Path.GetFileNameWithoutExtension(path),
        RawFile = manifest.Raw.Path,
        Size = $"{manifest.Width} x {manifest.Height}",
        ColorModel = manifest.ColorModel ?? "",
        ChannelOrder = manifest.ChannelOrder ?? "",
        Subsampling = manifest.Subsampling ?? "",
        BitDepth = manifest.BitDepth.ToString(),
        Range = manifest.Range ?? "",
        Matrix = manifest.Matrix ?? "",
        Storage = manifest.Storage ?? "",
        Alignment = manifest.Alignment ?? "",
        RawBytes = manifest.RawBytes.ToString(),
        Sha256 = manifest.Raw.Sha256 ?? "未指定",
    };

    private static Bitmap LoadRgb8(string rawPath, ManifestInfo manifest)
    {
        if (!File.Exists(rawPath))
            throw new FileNotFoundException("manifestが指すRAWファイルがありません。", rawPath);

        var strideBytes = checked(manifest.Width * 3);
        var isPlanar = string.Equals(manifest.Storage, "planar", StringComparison.OrdinalIgnoreCase);
        var expectedMinimum = isPlanar
            ? checked(manifest.Width * manifest.Height * 3)
            : checked(strideBytes * manifest.Height);
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
                    var source = isPlanar ? pixel : pixel * 3;
                    var target = destination + x * 3;
                    var r = isPlanar ? data[source] : data[source + (bgr ? 2 : 0)];
                    var g = isPlanar ? data[source + manifest.Width * manifest.Height] : data[source + 1];
                    var b = isPlanar ? data[source + 2 * manifest.Width * manifest.Height] : data[source + (bgr ? 0 : 2)];
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

    private void SavePng()
    {
        if (_currentBitmap is null)
            return;

        using var dialog = new SaveFileDialog
        {
            Filter = "PNG画像 (*.png)|*.png",
            FileName = Path.GetFileNameWithoutExtension(_currentManifestPath ?? "preview") + ".png",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _currentBitmap.Save(dialog.FileName, ImageFormat.Png);
            _statusLabel.Text = $"保存しました: {dialog.FileName}";
        }
    }

    private void ReplaceBitmap(Bitmap? bitmap)
    {
        var old = _currentBitmap;
        _currentBitmap = bitmap;
        _preview.Image = bitmap;
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

            return $"{_manifest.Id ?? Path.GetFileName(_path)}  ({_manifest.Width}x{_manifest.Height})";
        }
    }
}
