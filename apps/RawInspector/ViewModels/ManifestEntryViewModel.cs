using System.Collections.ObjectModel;
using RawInspector.Models;

namespace RawInspector.ViewModels;

/// <summary>
/// manifest 1件ぶんです。読み込みに失敗したものも捨てずに保持します。
/// 壊れた入力をどう扱うかは、このツールで確認したいことのひとつだからです。
/// </summary>
public sealed class ManifestEntryViewModel : ObservableObject
{
    public required string Path { get; init; }
    public ManifestInfo? Manifest { get; init; }
    public string? Error { get; init; }

    private bool _isSelected;
    /// <summary>TreeViewItem.IsSelected と双方向で結びます（一覧から選ばせるため）。</summary>
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>
    /// 葉の項目なので開閉しませんが、見出しと同じ ItemContainerStyle を通るため持たせます。
    /// 無いとバインドが外れて出力に警告が出ます。
    /// </summary>
    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public bool IsLoaded => Manifest is not null;

    public bool SupportsPreview => Manifest?.SupportsPreview ?? false;

    public string Label => Manifest is null
        ? $"[読み込み不可] {System.IO.Path.GetFileName(Path)}"
        : $"{System.IO.Path.GetFileName(Manifest.Raw.Path)}  [{Manifest.ColorModel}, {Manifest.Storage}, {Manifest.BitDepth}bit, "
          + $"{ResolutionNames.Describe(Manifest.Width, Manifest.Height)}]";

    public string ToolTip => Error ?? Path;

    /// <summary>読み上げや自動テストから中身が分かるようにします（既定だと型名が出ます）。</summary>
    public override string ToString() => Label;

    public string GroupName => Manifest?.Pattern ?? "読み込みエラー";

    public string SizeKey => Manifest is null ? "" : $"{Manifest.Width} x {Manifest.Height}";

    public static ManifestEntryViewModel Load(string path)
    {
        try
        {
            return new ManifestEntryViewModel { Path = path, Manifest = ManifestInfo.Load(path) };
        }
        catch (Exception ex)
        {
            return new ManifestEntryViewModel { Path = path, Error = ex.Message };
        }
    }
}

/// <summary>パターン名でまとめた一覧の見出しです。</summary>
public sealed class PatternGroupViewModel : ObservableObject
{
    public required string Name { get; init; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private bool _isExpanded = true;
    /// <summary>TreeViewItem.IsExpanded と双方向で結びます（まとめて開閉するため）。</summary>
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public ObservableCollection<ManifestEntryViewModel> Entries { get; } = [];

    public string Header => $"{Name}  ({Entries.Count})";

    public override string ToString() => Header;
}
