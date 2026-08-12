using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RawInspector.Decoding;
using RawInspector.Models;
using RawInspector.ViewModels;

namespace RawInspector;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.FitRequested += (_, _) => Dispatcher.BeginInvoke(Fit, DispatcherPriority.Loaded);
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Tool)) UpdatePreviewCursor();
            if (e.PropertyName is nameof(MainViewModel.IsPixelGridVisible)
                or nameof(MainViewModel.ScalePercent)
                or nameof(MainViewModel.ChromaBlockWidth)) UpdatePixelGrid();
            if (e.PropertyName == nameof(MainViewModel.IsFullScreen)) ApplyFullScreen();
        };
        RestoreLayout();

        Loaded += (_, _) =>
        {
            // XAMLでIsCheckedの初期値を書いても、最初から未チェックの項目はイベントが飛びません。
            // 起動時の見え方をチェックボックスと合わせるため、ここで1回反映します。
            OnColumnToggled(this, new RoutedEventArgs());
            _viewModel.RestoreLastFolder();
            UpdatePreviewCursor();
        };

        Closing += (_, _) => SaveLayout();
    }

    // --- 前回の画面の形を覚える ---

    /// <summary>
    /// 前回終了したときの位置と大きさへ戻します。
    /// 画面の外へ出てしまう記録は捨てます（掴めなくなるため）。
    /// </summary>
    private void RestoreLayout()
    {
        if (UserLayout.Load() is not { } layout) return;

        // 並び順はウィンドウの位置とは関係がないので、位置の判定より先に戻します。
        // 画面の外にある記録を捨てるとき、一緒に捨ててしまわないようにです。
        if (layout.SortOrder is { } sort && _viewModel.SortOptions.Contains(sort))
            _viewModel.SortOrder = sort;

        if (!layout.FitsInside(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight))
            return;

        // 位置を指定するので、画面中央へ置く既定の動きは止めます。
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = layout.Left;
        Top = layout.Top;
        Width = layout.Width;
        Height = layout.Height;
        if (layout.IsMaximized) WindowState = WindowState.Maximized;

        // 左右の欄の幅も戻します。0 は「記録なし」なので既定のままにします。
        // 狭くしすぎた記録をそのまま復元しないよう、MinWidth 以上に収めます。
        if (layout.ListWidth > 0)
            ListColumn.Width = new GridLength(Math.Max(layout.ListWidth, ListColumn.MinWidth), GridUnitType.Pixel);
        if (layout.DetailWidth > 0)
            DetailColumn.Width = new GridLength(Math.Max(layout.DetailWidth, DetailColumn.MinWidth), GridUnitType.Pixel);
    }

    private void SaveLayout()
    {
        // 最大化中は Left/Top/Width/Height が最大化後の値になるため、
        // 元に戻したときの大きさ（RestoreBounds）を覚えます。
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        new UserLayout
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized,
            ListWidth = ListColumn.ActualWidth,
            DetailWidth = DetailColumn.ActualWidth,
            SortOrder = _viewModel.SortOrder,
        }.Save();
    }

    private void OnManifestSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ManifestEntryViewModel entry)
            _viewModel.SelectedEntry = entry;
    }

    // --- 全画面 ---
    //
    // 左右の欄は Collapsed にしても、列の幅（340px など）はそのまま残ります。
    // 幅も 0 にしないと、絵の左右に空白の帯が残ってしまいます。
    // 戻したときに元の幅へ返せるよう、畳む前に控えておきます。

    private GridLength? _listWidthBeforeFullScreen;
    private GridLength? _detailWidthBeforeFullScreen;
    private WindowStyle _styleBeforeFullScreen;
    private WindowState _stateBeforeFullScreen;

    private void ApplyFullScreen()
    {
        if (_viewModel.IsFullScreen)
        {
            _listWidthBeforeFullScreen = ListColumn.Width;
            _detailWidthBeforeFullScreen = DetailColumn.Width;
            _styleBeforeFullScreen = WindowStyle;
            _stateBeforeFullScreen = WindowState;

            ListColumn.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(0);

            // 枠まで消して画面いっぱいにします。最大化のままだとタイトルバーが残ります。
            // 一度 Normal へ戻してから掛け直さないと、最大化中は枠の変更が効きません。
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            if (_listWidthBeforeFullScreen is { } list) ListColumn.Width = list;
            if (_detailWidthBeforeFullScreen is { } detail) DetailColumn.Width = detail;
            WindowStyle = _styleBeforeFullScreen;
            WindowState = _stateBeforeFullScreen;
        }
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => Fit();

    /// <summary>
    /// 生成の窓を開きます。作り終えたらフォルダを読み直して、できたものを選びます。
    /// 出したまま作り続けられるよう、閉じるまで使い回します。
    /// </summary>
    private GeneratorWindow? _generatorWindow;

    private void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        if (_generatorWindow is { IsLoaded: true })
        {
            _generatorWindow.Activate();
            return;
        }

        _generatorWindow = new GeneratorWindow(_viewModel.GeneratedFolder, OnGenerated) { Owner = this };
        _generatorWindow.Closed += (_, _) => _generatorWindow = null;
        _generatorWindow.Show();
    }

    private void OnGenerated(string manifestPath) => _viewModel.AdoptGenerated(manifestPath);

    /// <summary>
    /// 画像全体が表示領域へ収まる倍率にします。表示領域の大きさはビューしか知らないため、
    /// ViewModelではなくここで計算します。
    /// </summary>
    private void Fit()
    {
        if (_viewModel.PreviewPixelWidth <= 0 || _viewModel.PreviewPixelHeight <= 0) return;

        var viewportWidth = PreviewScroll.ViewportWidth;
        var viewportHeight = PreviewScroll.ViewportHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0) return;

        // スクロールバーが出入りして揺れないよう、少しだけ余裕を取ります。
        const double margin = 8;
        var scale = Math.Min(
            (viewportWidth - margin) / _viewModel.PreviewPixelWidth,
            (viewportHeight - margin) / _viewModel.PreviewPixelHeight) * 100.0;

        _viewModel.ScalePercent = scale;
        PreviewScroll.ScrollToHorizontalOffset(0);
        PreviewScroll.ScrollToVerticalOffset(0);
    }

    private void OnScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 手動で倍率を変えたあとに勝手に戻ると操作を奪うため、ここでは何もしません。
        // 全体表示は「全体表示」ボタンか、RAWを読み込んだ直後だけ行います。
    }

    /// <summary>
    /// ホイールでの拡大縮小です。カーソルの下にある画素をその場に留めたまま倍率を変えます。
    /// 気になる画素を指してから拡大する、という手順がそのまま通るようにするためです。
    ///
    /// 虫眼鏡モードならホイールだけで、手のひらモードならCtrl+ホイールで効きます。
    /// 手のひらモードで修飾キー無しのときは、通常のスクロールとしてScrollViewerへ渡します。
    /// </summary>
    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        var zooming = _viewModel.Tool == PreviewTool.Zoom || Keyboard.Modifiers == ModifierKeys.Control;
        if (!zooming) return;

        e.Handled = true;

        if (!_viewModel.HasPreview)
        {
            if (e.Delta > 0) _viewModel.ZoomInCommand.Execute(null);
            else _viewModel.ZoomOutCommand.Execute(null);
            return;
        }

        ZoomAnchored(e.GetPosition(PreviewScroll), e.Delta > 0);
    }

    // --- 手のひらでの移動 ---

    private Point _dragStart;
    private double _dragStartHorizontalOffset;
    private double _dragStartVerticalOffset;
    private bool _dragging;

    private void OnPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Tool != PreviewTool.Hand || !_viewModel.HasPreview) return;

        _dragStart = e.GetPosition(PreviewScroll);
        _dragStartHorizontalOffset = PreviewScroll.HorizontalOffset;
        _dragStartVerticalOffset = PreviewScroll.VerticalOffset;
        _dragging = true;
        PreviewScroll.CaptureMouse();
        UpdatePreviewCursor();
        e.Handled = true;
    }

    private void OnPreviewLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        PreviewScroll.ReleaseMouseCapture();
        UpdatePreviewCursor();
    }

    /// <summary>
    /// ドラッグ中の移動です。掴んだ点が指に付いてくるように、
    /// カーソルの移動量とは**逆向き**にスクロールします。
    /// </summary>
    private void OnPreviewScrollMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var current = e.GetPosition(PreviewScroll);
        PreviewScroll.ScrollToHorizontalOffset(_dragStartHorizontalOffset - (current.X - _dragStart.X));
        PreviewScroll.ScrollToVerticalOffset(_dragStartVerticalOffset - (current.Y - _dragStart.Y));
    }

    /// <summary>
    /// 虫眼鏡カーソルです。WPFの標準カーソルには虫眼鏡が無いため、
    /// tools/make_cursor.py で作った .cur を読み込みます。
    /// 読み込めなかった場合は十字で代用します（カーソルのために起動を止める理由はないため）。
    /// </summary>
    private static readonly Cursor ZoomCursor = LoadZoomCursor();

    private static Cursor LoadZoomCursor()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/zoom.cur", UriKind.Absolute);
            using var stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
            return new Cursor(stream);
        }
        catch
        {
            return Cursors.Cross;
        }
    }

    /// <summary>モードが分かるようにカーソルを変えます。</summary>
    // --- 画素の境目 ---
    //
    // 線を1色で引くと、その色に近い画素の上で消えます。白い絵なら白い線が、
    // 黒い絵なら黒い線が見えません。ここでは同じ位置に白と黒の破線を
    // 半周期ずらして重ね、どちらかが必ず見えるようにします。
    //
    // 1本のブラシをタイル状に敷き詰めるので、線の数がいくつになっても
    // 図形は1組だけです。1画素ごとに線の要素を作ると、拡大したときに数万個になります。

    private void UpdatePixelGrid() =>
        PixelGridOverlay.Fill = _viewModel.IsPixelGridVisible
            ? PixelGrid.Build(_viewModel.PointsPerPixel, _viewModel.ChromaBlockWidth, _viewModel.ChromaBlockHeight)
            : null;

    private void UpdatePreviewCursor() =>
        PreviewScroll.Cursor = _viewModel.Tool switch
        {
            PreviewTool.Hand when _dragging => Cursors.ScrollAll,
            PreviewTool.Hand => Cursors.Hand,
            PreviewTool.Zoom => ZoomCursor,
            _ => Cursors.Arrow,
        };

    private void OnZoomInExecuted(object sender, ExecutedRoutedEventArgs e) => ZoomFromCenter(true);

    private void OnZoomOutExecuted(object sender, ExecutedRoutedEventArgs e) => ZoomFromCenter(false);

    /// <summary>
    /// ボタンやキーボードからの拡大縮小では、表示領域の中央を動かさずに倍率を変えます。
    /// 左上を固定すると、全体表示から拡大したときに必ず左上隅へ飛んでしまうためです。
    /// </summary>
    private void ZoomFromCenter(bool zoomIn)
    {
        if (!_viewModel.HasPreview)
        {
            if (zoomIn) _viewModel.ZoomInCommand.Execute(null);
            else _viewModel.ZoomOutCommand.Execute(null);
            return;
        }

        var center = new Point(PreviewScroll.ViewportWidth / 2, PreviewScroll.ViewportHeight / 2);
        ZoomAnchored(center, zoomIn);
    }

    /// <summary>
    /// <paramref name="anchor"/>（表示領域を基準にした座標）にある画素を、
    /// 倍率変更の前後で同じ位置に保ちます。
    /// </summary>
    private void ZoomAnchored(Point anchor, bool zoomIn)
    {
        // 倍率を変える前に、その位置がどの画素なのかを控えます。
        var beforeWidth = PreviewImageElement.ActualWidth;
        var beforeHeight = PreviewImageElement.ActualHeight;
        if (beforeWidth <= 0 || beforeHeight <= 0) return;

        var local = PreviewScroll.TranslatePoint(anchor, PreviewImageElement);
        var pixelX = local.X * _viewModel.PreviewPixelWidth / beforeWidth;
        var pixelY = local.Y * _viewModel.PreviewPixelHeight / beforeHeight;

        if (zoomIn) _viewModel.ZoomInCommand.Execute(null);
        else _viewModel.ZoomOutCommand.Execute(null);

        // 新しい大きさで配置し直してから、控えた画素が元の位置へ戻るようスクロールします。
        PreviewScroll.UpdateLayout();
        if (PreviewImageElement.ActualWidth <= 0) return;

        var target = new Point(
            pixelX * PreviewImageElement.ActualWidth / _viewModel.PreviewPixelWidth,
            pixelY * PreviewImageElement.ActualHeight / _viewModel.PreviewPixelHeight);
        var now = PreviewImageElement.TranslatePoint(target, PreviewScroll);

        PreviewScroll.ScrollToHorizontalOffset(PreviewScroll.HorizontalOffset + (now.X - anchor.X));
        PreviewScroll.ScrollToVerticalOffset(PreviewScroll.VerticalOffset + (now.Y - anchor.Y));
    }

    /// <summary>
    /// 右クリックの位置でプローブを更新してから、メニューを出します。
    /// メニューの先頭にその画素の値を出すため、クリック位置での読み取りを先に済ませます。
    /// </summary>
    private void OnPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        UpdateProbeFrom(e.GetPosition(PreviewImageElement));
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e) =>
        UpdateProbeFrom(e.GetPosition(PreviewImageElement));

    private void UpdateProbeFrom(Point imageLocalPoint)
    {
        if (!_viewModel.HasPreview) return;
        if (PreviewImageElement.ActualWidth <= 0 || PreviewImageElement.ActualHeight <= 0) return;

        var x = (int)(imageLocalPoint.X * _viewModel.PreviewPixelWidth / PreviewImageElement.ActualWidth);
        var y = (int)(imageLocalPoint.Y * _viewModel.PreviewPixelHeight / PreviewImageElement.ActualHeight);
        _viewModel.UpdateProbe(x, y);
    }

    /// <summary>
    /// カーソルがプレビューから外れたら、下のバーの表示を消します。
    /// 抜けぎわの画素が残っていると、いま指している値だと読み違えるためです。
    ///
    /// 消すのはバーだけで、右クリックメニューに出す値は残します。
    /// メニューへマウスを移すときにもここへ来るので、両方消すとメニューが「—」になってしまいます。
    /// </summary>
    private void OnPreviewMouseLeave(object sender, MouseEventArgs e) => _viewModel.ClearHover();

    /// <summary>
    /// 生成条件の表で、どの列を出すかを切り替えます。
    /// DataGridColumn は視覚ツリーに載らずバインドしにくいため、ここで直接触ります。
    /// </summary>
    private void OnColumnToggled(object sender, RoutedEventArgs e)
    {
        // XAMLの解析中にも Checked が飛ぶため、列がまだ作られていないことがあります。
        if (NameColumn is null || ValueColumn is null || KeyColumn is null) return;

        NameColumn.Visibility = ToVisibility(ShowNameColumn.IsChecked);
        ValueColumn.Visibility = ToVisibility(ShowValueColumn.IsChecked);
        KeyColumn.Visibility = ToVisibility(ShowKeyColumn.IsChecked);
    }

    /// <summary>
    /// 生成条件の表を最初の状態へ戻します。
    ///
    /// 列見出しを押すと並び替えられますが、押した状態が残ると
    /// 「なぜこの順なのか」が分からなくなります。既定の並びは
    /// パターン名・画像サイズ・色モデル…という確認の順に意味があるので、戻せるようにします。
    /// </summary>
    private void OnResetParameterViewClick(object sender, RoutedEventArgs e)
    {
        ShowNameColumn.IsChecked = true;
        ShowValueColumn.IsChecked = true;
        ShowKeyColumn.IsChecked = false;
        OnColumnToggled(sender, e);

        ParameterGrid.Items.SortDescriptions.Clear();
        foreach (var column in ParameterGrid.Columns) column.SortDirection = null;
        ParameterGrid.Items.Refresh();
    }

    private static Visibility ToVisibility(bool? isChecked) =>
        isChecked == true ? Visibility.Visible : Visibility.Collapsed;
}
