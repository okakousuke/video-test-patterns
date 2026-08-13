using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using RawInspector.Models;

namespace RawInspector.ViewModels;

/// <summary>素材フォルダ1つぶんの表示です。</summary>
public sealed class FolderCard : ObservableObject
{
    /// <summary>
    /// アイコンの場所です。絵文字はフォントに依存して環境ごとに形が変わるので、
    /// このリポジトリの中に画像を持ちます。
    /// </summary>
    public required string Icon { get; init; }

    public required string Title { get; init; }
    public required string Purpose { get; init; }
    public required string Path { get; init; }

    private WorkspaceScan? _scan;
    public WorkspaceScan? Scan
    {
        get => _scan;
        set
        {
            if (!Set(ref _scan, value)) return;
            Raise(nameof(Summary));
            Raise(nameof(HasProblem));
            Raise(nameof(CanOpen));
        }
    }

    public string Summary => _scan?.Summary ?? "数えています…";
    public bool HasProblem => _scan?.HasProblem ?? false;
    public bool CanOpen => _scan?.IsUsable ?? false;
}

/// <summary>
/// 一括生成の道具1つぶんです。
/// <c>SupportsDryRun</c> は「作らずに何ができるかだけ出せるか」です。
/// 持っていない道具に付けても効かないので、持っているものだけボタンを出します。
/// </summary>
public sealed record BatchTool(string Icon, string Title, string Purpose, string Script, bool SupportsDryRun)
{
    /// <summary>
    /// 何をどれだけ作るのか。押す前に読めるところへ置きます。
    /// 走らせてからでは、消すまで場所を取り続けるからです。
    /// </summary>
    public string Detail { get; init; } = "";

    /// <summary>できるものの目安（件数と容量）。実際の数は下の記録に出ます。</summary>
    public string Yield { get; init; } = "";
}

/// <summary>
/// 画面を開く入口です。押すと窓が出て、そこから先は手で操作します。
/// スクリプトを回す道具（<see cref="BatchTool"/>）とは扱いを分けています。
/// 押した先に画面があるのか、黙って走り出すのかは、押す前に分かるべきだからです。
/// </summary>
public sealed record AppEntry(string Key, string Icon, string Title, string Purpose, string Detail);

/// <summary>
/// 開いたときに最初に出す画面です。
///
/// **起動するだけの入口にはしません。** それだと `--help` を写しただけのものになり、
/// 道具が増えるたびに更新漏れで古くなります。
/// ここに出すのは、道具を1つずつ叩いても分からないことに絞ります。
///
/// - 素材がどこに何件あって、壊れているものが無いか
/// - 生成器へ繋がっているか（繋がらないと、生成の窓を開いてから初めて気付きます）
/// - 一括生成を、作る前に件数と容量を見てから走らせられるか
/// </summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly Action<string> _openFolder;
    private readonly Action<string> _openApp;

    public DashboardViewModel(Action<string> openFolder, Action<string> openApp)
    {
        _openFolder = openFolder;
        _openApp = openApp;
        OpenFolderCommand = new RelayCommand<FolderCard>(card => _openFolder(card.Path), card => card.CanOpen);
        OpenAppCommand = new RelayCommand<AppEntry>(entry => _openApp(entry.Key));
        RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsBusy);
        RunToolCommand = new RelayCommand<BatchTool>(tool => _ = RunToolAsync(tool, dryRun: false), _ => !IsBusy && HasRepository);
        PreviewToolCommand = new RelayCommand<BatchTool>(tool => _ = RunToolAsync(tool, dryRun: true),
            tool => !IsBusy && HasRepository && tool.SupportsDryRun);
    }

    public RelayCommand<FolderCard> OpenFolderCommand { get; }
    public RelayCommand<AppEntry> OpenAppCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand<BatchTool> RunToolCommand { get; }
    public RelayCommand<BatchTool> PreviewToolCommand { get; }

    public ObservableCollection<FolderCard> Folders { get; } = [];

    /// <summary>
    /// 画面を開く入口です。押すと窓が出ます。
    /// </summary>
    public IReadOnlyList<AppEntry> Apps { get; } =
    [
        new("viewer", "icons/samples.png", "RAWを見る",
            "manifest とRAWの入ったフォルダを開いて、1本ずつ確かめます。",
            "manifest に書かれた寸法・色形式・格納形式のとおりに復号して映します。"
            + "変換係数やレンジを手で変えて見比べたり、成分を1つずつ伏せたり、"
            + "画素の格子を出して色差がどのます目で共有されているかまで追えます。"
            + "下のフォルダから選ぶか、ここを押して場所を指定してください。"),
        new("generator", "icons/gen-ok.png", "パターンを作る",
            "条件を選んで1本作ります。作ったものはそのまま一覧へ入ります。",
            "パターン・寸法・色形式・格納形式を選ぶと、成立しない組み合わせはその場で理由が出ます。"
            + "パターンごとの細かい設定（段数・周期・色など）も、生成器が申告してきた範囲つきで並びます。"
            + "実行するコマンドは画面に出したままなので、同じものを端末で再現できます。"),
    ];

    /// <summary>
    /// 画面を持たない道具です。押すとスクリプトがそのまま走ります。
    /// `--dry-run` を持つものは、作る前に件数と容量を出せます。
    /// 数百MBになるものがあるので、走らせてから気付くのは避けたいところです。
    /// </summary>
    public IReadOnlyList<BatchTool> Tools { get; } =
    [
        new("icons/patterns.png", "全パターンを1本ずつ",
            "42パターンを、格納形式とサイズを散らした条件で1本ずつ作ります。",
            "tools/make_samples.py", false)
        {
            Detail = "パターンの一覧そのものを目で見たいときに使います。"
                     + "1パターンにつき1本だけ作り、格納形式と寸法はパターンごとに変えます。"
                     + "同じ絵を形式違いで並べたいときは「格納形式を網羅」のほうです。",
            Yield = "42本ほど。小さめの寸法なので数十MB程度です。",
        },
        new("icons/variants.png", "条件を振って量産",
            "塗り違い・サイズ違い・形式違いを量産します（raster / cards / resolution の3群）。",
            "tools/make_variant_raws.py", true)
        {
            Detail = "raster は単色の塗りつぶしを15色ぶんと、4:4:4 / 4:2:2 / 4:2:0 の比較を作ります"
                     + "（平坦な面はどの間引きでも同じに出るはずで、違えば処理側の問題です）。"
                     + "cards はカラーバー・モノスコープ・デジタルカードを標準的な解像度で、"
                     + "resolution は解像度系9パターンを3寸法×3形式で作ります。"
                     + "先に「先に数える」を押して、件数と容量を見てから実行してください。",
            Yield = "百数十本。合計で数百MBになります。",
        },
        new("icons/storage.png", "格納形式を網羅",
            "同じ絵を全形式で出します。形式ごとの違いだけを見るためのものです。",
            "tools/make_reference_raws.py", false)
        {
            Detail = "絵とサイズを固定して、planar / packed / NV12 / P010 / v210 / MIPI10 などを一通り出します。"
                     + "絵が同じなので、読み手側の実装で崩れたときに、形式の解釈だけが原因だと切り分けられます。"
                     + "既定は小さい寸法です。HD・FHD・4K も指定できますが、4Kは1本で511MBになります。",
            Yield = "小さい寸法なら20本ほどで数MBです。",
        },
    ];

    public bool HasRepository => RepositoryLocator.Find() is not null;

    public string RepositoryText => RepositoryLocator.Find() ?? "リポジトリが見つかりません（実行ファイルだけを別の場所へ置いた場合など）";

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
            RunToolCommand.RaiseCanExecuteChanged();
            PreviewToolCommand.RaiseCanExecuteChanged();
        }
    }

    private string _generatorText = "確かめています…";
    public string GeneratorText { get => _generatorText; private set => Set(ref _generatorText, value); }

    private bool _generatorReady;
    public bool GeneratorReady
    {
        get => _generatorReady;
        private set { if (Set(ref _generatorReady, value)) Raise(nameof(GeneratorNotReady)); }
    }

    public bool GeneratorNotReady => !_generatorReady;

    private string _log = "";
    public string Log
    {
        get => _log;
        private set { if (Set(ref _log, value)) Raise(nameof(HasLog)); }
    }

    public bool HasLog => !string.IsNullOrWhiteSpace(_log);

    /// <summary>素材の場所と、生成器の状態を数え直します。</summary>
    public async Task RefreshAsync(string? lastFolder = null)
    {
        IsBusy = true;
        Log = "";
        try
        {
            BuildFolderCards(lastFolder);

            // 数えるのはファイル走査なので、画面を止めないよう別のところで回します。
            await Task.WhenAll(Folders.Select(async card =>
            {
                var scan = await Task.Run(() => WorkspaceScan.Scan(card.Path));
                card.Scan = scan;
            }));
            OpenFolderCommand.RaiseCanExecuteChanged();

            await CheckGeneratorAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildFolderCards(string? lastFolder)
    {
        Folders.Clear();

        // 役割ごとに分けて出します。同じ「RAWの置き場」でも意味が違うためです。
        if (RepositoryLocator.Resolve("generated") is { } generated)
            Folders.Add(new FolderCard
            {
                Icon = "icons/generated.png",
                Title = "generated",
                Purpose = "作って捨てる場所です。.gitignore の対象で、リポジトリには残りません。",
                Path = generated,
            });

        if (RepositoryLocator.Resolve("samples", "raw") is { } samples)
            Folders.Add(new FolderCard
            {
                Icon = "icons/samples.png",
                Title = "samples/raw",
                Purpose = "リポジトリに同梱している参照用です。読み手側の実装の入力テストにそのまま使えます。",
                Path = samples,
            });

        if (lastFolder is { Length: > 0 } && Folders.All(f => !SamePath(f.Path, lastFolder)))
            Folders.Add(new FolderCard
            {
                Icon = "icons/recent.png",
                Title = "前回開いていた場所",
                Purpose = "前回このアプリで開いていた場所です。続きから見るときはここからどうぞ。",
                Path = lastFolder,
            });
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 生成器へ繋がるかを確かめます。
    /// 繋がらないことに気付くのが「生成の窓を開いたとき」だと遅いので、ここで先に出します。
    /// </summary>
    private async Task CheckGeneratorAsync()
    {
        try
        {
            var catalog = await GeneratorCatalog.LoadAsync("python -m vtp");
            GeneratorReady = true;
            GeneratorText = $"{catalog.Generator}　パターン {catalog.Patterns.Count} 種 / "
                            + $"成立する組み合わせ {catalog.Combinations.Count} 通り / 格納形式 {catalog.Storages.Count} 種";
        }
        catch (Exception ex)
        {
            GeneratorReady = false;
            GeneratorText = ex.Message;
        }
    }

    /// <summary>
    /// 一括生成の道具を走らせます。
    /// 先に見積り（--dry-run）を出せるものは、作る前に件数と容量を確かめられます。
    /// </summary>
    private async Task RunToolAsync(BatchTool tool, bool dryRun)
    {
        if (RepositoryLocator.Find() is not { } root) return;

        IsBusy = true;
        Log = dryRun ? "何ができるかを確かめています…" : "生成しています…（本数によっては数分かかります）";
        try
        {
            var arguments = new List<string> { Path.Combine(root, tool.Script.Replace('/', Path.DirectorySeparatorChar)) };
            if (dryRun) arguments.Add("--dry-run");

            var (exitCode, stdout, stderr) = await GeneratorCatalog.RunAsync("python", arguments, root);
            Log = string.Join("\n", new[] { stdout.TrimEnd(), stderr.TrimEnd() }.Where(s => s.Length > 0));
            if (exitCode != 0) Log = $"（終了コード {exitCode}）\n{Log}";

            if (!dryRun) await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>リポジトリの場所をエクスプローラーで開きます。</summary>
    public void OpenRepository()
    {
        if (RepositoryLocator.Find() is not { } root) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        }
        catch
        {
            // 開けなくても困るのはこの操作だけなので、黙って諦めます。
        }
    }
}
