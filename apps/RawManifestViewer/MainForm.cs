using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RawManifestViewer;

public sealed class MainForm : Form
{
    private readonly Button _openFolderButton = new() { Text = "フォルダを開く", AutoSize = true };
    private readonly Label _folderLabel = new() { Text = "フォルダ未選択", AutoEllipsis = true, Dock = DockStyle.Fill };
    private readonly ListBox _manifestList = new() { Dock = DockStyle.Fill };
    private readonly PropertyGrid _propertyGrid = new() { Dock = DockStyle.Fill, HelpVisible = true, ToolbarVisible = false };
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

        if (!manifest.SupportsPreview)
        {
            ReplaceBitmap(null);
            _savePngButton.Enabled = false;
            _statusLabel.Text = $"読み込み済み（プレビュー未対応）: {manifest.ColorModel}, {manifest.BitDepth}bit, {manifest.Storage}";
            return;
        }

        try
        {
            var rawPath = manifest.ResolveRawPath(entry.Path);
            var bitmap = LoadPreview(rawPath, manifest);
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
