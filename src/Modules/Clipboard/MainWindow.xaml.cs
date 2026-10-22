using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ClipboardApp;

public partial class MainWindow : Window
{
    private readonly HistoryStore _store;
    private readonly ICollectionView _view;
    private IntPtr _previousWindow;

    // [ToolHub] 항목 확장: 한 열 = 10개(1~9,0), Tab 으로 오른쪽에 다음 열들을 펼친다.
    private const int PageSize = 10;
    private const int ExpandedColumns = 3;
    private const double ColumnWidth = 300;
    private const double DotsColumnWidth = 16; // 접힘 상태의 세로 점 영역
    private const double ChromeWidth = 34;   // 바깥 여백 + 테두리 + 안쪽 여백
    private const double ChromeHeight = 140; // 검색창 + 안내문 + 점 표시 + 여백
    private const double MaxRowHeight = 34;  // 이미지 항목(썸네일 28 + 여백) 기준 고정 행 높이
    private bool _expanded;
    private int _firstPage;
    private ScrollViewer? _scroll;

    private int Columns => _expanded ? ExpandedColumns : 1;
    private int ActivePage => Math.Max(List.SelectedIndex, 0) / PageSize;

    public MainWindow(HistoryStore store)
    {
        InitializeComponent();
        Resources["ColW"] = ColumnWidth;
        Resources["RowH"] = MaxRowHeight;
        Resources["PanelH"] = MaxRowHeight * PageSize;
        ApplyTheme();
        _store = store;
        _view = CollectionViewSource.GetDefaultView(store.Items);
        _view.Filter = o => string.IsNullOrWhiteSpace(SearchBox.Text) || ((ClipItem)o).Matches(SearchBox.Text.Trim());
        List.ItemsSource = _view;
        store.Items.CollectionChanged += (_, _) => UpdateNumbers();
    }


    // [ToolHub] 테마: "system"(윈도우 앱 모드 따름) / "light" / "dark"
    public string ThemeMode { get; set; } = "system";

    private static readonly string[][] DarkPalette =
    {
        new[] { "BgBrush", "#202022" }, new[] { "LineBrush", "#4A4A4E" }, new[] { "BoxBrush", "#2B2B2E" },
        new[] { "TextBrush", "#FFFFFF" }, new[] { "AccentBrush", "#6CB6FF" }, new[] { "ThumbBrush", "#111113" },
        new[] { "SubBrush", "#A0A0A8" }, new[] { "DimBrush", "#80808A" }, new[] { "HoverBrush", "#3A3A3E" },
        new[] { "SelBrush", "#0F5FA8" },
    };

    private static readonly string[][] LightPalette =
    {
        new[] { "BgBrush", "#F7F7F9" }, new[] { "LineBrush", "#CFCFD6" }, new[] { "BoxBrush", "#FFFFFF" },
        new[] { "TextBrush", "#1B1B1F" }, new[] { "AccentBrush", "#0B63C5" }, new[] { "ThumbBrush", "#E6E6EA" },
        new[] { "SubBrush", "#5A5A63" }, new[] { "DimBrush", "#76767F" }, new[] { "HoverBrush", "#E9E9EE" },
        new[] { "SelBrush", "#BFDDF5" },
    };

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    private void ApplyTheme()
    {
        var light = ThemeMode == "light" || (ThemeMode != "dark" && SystemUsesLightTheme());
        foreach (var entry in light ? LightPalette : DarkPalette)
            Resources[entry[0]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(entry[1]));
    }

    // [ToolHub] 페이지 점 표시: 접힘=세로(오른쪽), 펼침=가로(하단). 현재 열=강조, 펼쳐서 보이는 열=보통.
    private void UpdateDots()
    {
        var total = Math.Max(1, (List.Items.Count + PageSize - 1) / PageSize);
        var active = ActivePage;
        FillDots(VDots, Orientation.Vertical, total, active, active, active);
        FillDots(HDots, Orientation.Horizontal, total, active, _firstPage, _firstPage + Columns - 1);
    }

    private static void FillDots(StackPanel panel, Orientation orientation, int total, int active, int from, int to)
    {
        panel.Children.Clear();
        if (total <= 1) return;
        for (var p = 0; p < total; p++)
        {
            var dot = new Ellipse
            {
                Width = 7, Height = 7,
                Margin = orientation == Orientation.Vertical ? new Thickness(0, 3, 0, 3) : new Thickness(3, 0, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = p == active ? 1 : p >= from && p <= to ? 0.75 : 0.4,
            };
            dot.SetResourceReference(Shape.FillProperty, p == active ? "AccentBrush" : "SubBrush");
            panel.Children.Add(dot);
        }
    }

    // [ToolHub] 다중 모니터: 마우스 커서가 있는 모니터의 작업 영역(작업 표시줄 위) 하단 중앙에 띄운다.
    private System.Drawing.Rectangle _waPx;
    private double _scale = 1;
    private double _heightDip;

    private double WidthDip => Columns * ColumnWidth + ChromeWidth + (_expanded ? 0 : DotsColumnWidth);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    private static double MonitorScale(System.Drawing.Rectangle bounds)
    {
        try
        {
            var mon = MonitorFromPoint(new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), 2);
            if (GetDpiForMonitor(mon, 0, out var dpi, out _) == 0 && dpi > 0) return dpi / 96.0;
        }
        catch { }
        return 1;
    }

    /// <summary>창을 대상 모니터 기준 픽셀 좌표로 정확히 배치한다(모니터마다 DPI 가 달라도 맞도록).</summary>
    private void PlaceWindow()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        var w = (int)Math.Round(WidthDip * _scale);
        var h = (int)Math.Round(_heightDip * _scale);
        var x = _waPx.Left + (_waPx.Width - w) / 2;
        var y = _waPx.Bottom - h - (int)Math.Round(8 * _scale);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, 0x0004 | 0x0010);
    }
    /// <summary>열려 있으면 닫고, 닫혀 있으면 연다 (Win+V).</summary>
    public void TogglePopup()
    {
        if (IsVisible) Hide();
        else ShowPopup();
    }

    /// <summary>Shows the popup near the bottom of the screen and focuses the search box.</summary>
    public void ShowPopup()
    {
        _previousWindow = Native.GetForegroundWindow();

        ApplyTheme();
        SearchBox.Text = "";
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        _waPx = screen.WorkingArea;
        _scale = MonitorScale(screen.Bounds);
        var areaHeight = _waPx.Height / _scale;
        // 한 열 10개가 모두 보이도록 행 높이를 정한다(작은 화면이면 줄인다).
        var rowH = Math.Clamp((areaHeight - 16 - ChromeHeight) / PageSize, 30, MaxRowHeight);
        Resources["RowH"] = rowH;
        Resources["PanelH"] = rowH * PageSize;
        _heightDip = rowH * PageSize + ChromeHeight;
        Height = _heightDip;
        _firstPage = 0;
        SetExpanded(false);

        UpdateNumbers();
        Show();
        PlaceWindow();
        Activate();
        Dispatcher.BeginInvoke(PlaceWindow, System.Windows.Threading.DispatcherPriority.Loaded); // DPI 가 다른 모니터로 옮겨진 뒤 한 번 더 보정
        List.Focus(); // 검색창은 클릭했을 때만 입력 (숫자 키는 바로 붙여넣기에 쓰인다)
        if (List.Items.Count > 0) List.SelectedIndex = 0;
        ScrollToPage();
    }

    private void Window_Deactivated(object? sender, EventArgs e) => Hide();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 1..9, 0 (검색창에 입력 중이면 Ctrl+숫자) 로 현재 열의 1~10번째 항목을 바로 붙여넣는다.
        if (DigitIndex(e.Key) is int n &&
            Keyboard.Modifiers == (SearchBox.IsKeyboardFocused ? ModifierKeys.Control : ModifierKeys.None))
        {
            e.Handled = true;
            var target = ActivePage * PageSize + n;
            if (target < List.Items.Count && List.Items[target] is ClipItem quick) _ = PasteAsync(quick);
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (SearchBox.IsKeyboardFocused)
                {
                    // 검색창에서 Esc: 검색어를 지우고 목록으로 돌아간다(한 번 더 누르면 닫힘).
                    SearchBox.Text = "";
                    List.Focus();
                }
                else Hide();
                e.Handled = true;
                break;
            case Key.Oem2 or Key.Divide when Keyboard.Modifiers == ModifierKeys.None && !SearchBox.IsKeyboardFocused:
                SearchBox.Focus();
                e.Handled = true;
                break;
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.None:
                SetExpanded(!_expanded);
                e.Handled = true;
                break;
            case Key.Right when _expanded:
                MovePage(1);
                e.Handled = true;
                break;
            case Key.Left when _expanded:
                MovePage(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MovePage(1);
                e.Handled = true;
                break;
            case Key.PageUp:
                MovePage(-1);
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (List.SelectedItem is ClipItem sel) _ = PasteAsync(sel);
                e.Handled = true;
                break;
            case Key.Delete when SearchBox.Text.Length == 0 || !SearchBox.IsKeyboardFocused:
                if (List.SelectedItem is ClipItem del) Delete(del);
                e.Handled = true;
                break;
        }
    }

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        if (expanded) _firstPage = ActivePage; // 현재 열부터 오른쪽으로 다음 내역을 펼친다
        Width = Columns * ColumnWidth + ChromeWidth + (expanded ? 0 : DotsColumnWidth);
        VDots.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        HDots.Visibility = expanded ? Visibility.Visible : Visibility.Hidden;
        PlaceWindow();
        ScrollToPage();
    }

    /// <summary>선택 항목의 열이 보이도록 열 단위로 가로 스크롤한다.</summary>
    private void ScrollToPage()
    {
        var page = ActivePage;
        if (page < _firstPage) _firstPage = page;
        else if (page >= _firstPage + Columns) _firstPage = page - Columns + 1;

        List.ApplyTemplate();
        _scroll ??= FindChild<ScrollViewer>(List);
        List.UpdateLayout();
        _scroll?.ScrollToHorizontalOffset(_firstPage * ColumnWidth);
        UpdateDots();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateNumbers();
        ScrollToPage();
    }

    /// <summary>Labels the items of the cursor's column 1..9,0 to match the Ctrl+digit shortcuts.</summary>
    private void UpdateNumbers()
    {
        var start = ActivePage * PageSize;
        var i = 0;
        foreach (ClipItem item in _view)
        {
            item.Number = i >= start && i < start + PageSize ? ((i - start + 1) % 10).ToString() : "";
            i++;
        }
        UpdateDots();
    }

    private static int? DigitIndex(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D1,
        Key.D0 => 9,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
        Key.NumPad0 => 9,
        _ => null,
    };

    /// <summary>열 단위 이동. 맨 오른쪽(마지막) 열에서 한 번 더 오른쪽이면 맨 처음 항목으로, 첫 열에서 왼쪽이면 맨 마지막 항목으로 돌아간다.</summary>
    private void MovePage(int dir)
    {
        var count = List.Items.Count;
        if (count == 0) return;
        if (dir > 0 && ActivePage >= (count - 1) / PageSize) List.SelectedIndex = 0;
        else if (dir < 0 && ActivePage == 0) List.SelectedIndex = count - 1;
        else MoveSelection(dir * PageSize);
    }

    private void MoveSelection(int delta)
    {
        if (List.Items.Count == 0) return;
        var idx = Math.Clamp(List.SelectedIndex + delta, 0, List.Items.Count - 1);
        List.SelectedIndex = idx;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        UpdateNumbers();
        if (List.Items.Count > 0) List.SelectedIndex = 0;
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (ItemsControl.ContainerFromElement(List, src) is ListBoxItem { DataContext: ClipItem item })
            _ = PasteAsync(item);
    }

    private void Delete(ClipItem item)
    {
        var idx = List.SelectedIndex;
        _store.Remove(item);
        if (List.Items.Count > 0) List.SelectedIndex = Math.Min(idx, List.Items.Count - 1);
    }

    /// <summary>마지막으로 직접 붙여넣은 시각(TickCount64). 모듈이 자기가 만든 클립보드 변경 알림을 건너뛰는 데 쓴다.</summary>
    public long LastPasteTick { get; private set; }

    private async Task PasteAsync(ClipItem item)
    {
        LastPasteTick = Environment.TickCount64;
        if (!TrySetClipboard(item)) return;

        // 붙여넣기를 먼저 보내고, 목록 갱신/저장은 그 뒤에 한다.
        Hide();
        var target = _previousWindow;
        if (target != IntPtr.Zero)
        {
            Native.SetForegroundWindow(target);
            for (var i = 0; i < 20 && Native.GetForegroundWindow() != target; i++)
                await Task.Delay(8);
        }
        await Task.Delay(15);
        Native.SendCtrlV();

        // The used item goes to the top of the history.
        _store.MoveToTop(item);
    }

    private static bool TrySetClipboard(ClipItem item)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (item.IsImage)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(item.ImagePath!);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    Clipboard.SetImage(bmp);
                }
                else
                {
                    Clipboard.SetText(item.Text ?? "");
                }
                return true;
            }
            catch (COMException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
        return false;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var d = child; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }
}
