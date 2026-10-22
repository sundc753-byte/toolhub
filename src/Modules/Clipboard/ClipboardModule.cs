using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToolHub;
using Clipboard = System.Windows.Clipboard;

namespace ClipboardApp;

/// <summary>
/// Win+V 클립보드 기록. 원본: github.com/sundc753-byte/clipboard (브랜치 claude/clipboard-app, b14382c).
/// 원본 App.xaml.cs 의 동작을 그대로 옮기되, 트레이 아이콘/자동 시작은 허브가 담당하고
/// WPF 창과 훅은 이 모듈 전용 STA 스레드(Dispatcher)에서 실행한다.
/// </summary>
public sealed class ClipboardModule : ModuleBase
{
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private HistoryStore? _store;
    private MainWindow? _window;
    private KeyboardHook? _hook;
    private ClipboardMonitor? _monitor;

    public override string Id => "clipboard";
    public override string DisplayName => "클립보드 (Win+V)";
    public override string Description => "클립보드 기록(텍스트/이미지+OCR 검색). Win+V 로 열기, 기록은 %AppData%\\ClipboardApp";
    public override string[] ConflictProcessNames => new[] { "ClipboardApp" };
    public override string[] LegacyRunValueNames => new[] { "ClipboardApp" };

    protected override void OnStart()
    {
        Exception? failure = null;
        using var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _store = new HistoryStore();
                _window = new MainWindow(_store) { ThemeMode = Host.GetSetting(Id, "theme", "system") };

                _monitor = new ClipboardMonitor();
                _monitor.Changed += () => _dispatcher.BeginInvoke(OnClipboardChanged);

                _dispatcher.BeginInvoke(MaintainStoredImages);

                try
                {
                    _hook = new KeyboardHook();
                    _hook.WinVPressed += () => _dispatcher.BeginInvoke(_window.TogglePopup);
                }
                catch (Exception ex)
                {
                    Host.Log(Id, "Win+V 단축키 등록 실패: " + ex.Message);
                    Host.Notify("클립보드", "Win+V 단축키를 등록하지 못했습니다. 트레이 메뉴에서 열 수 있습니다.");
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ready.Set();
            }

            if (failure == null) Dispatcher.Run();

            // Dispatcher 종료 후 정리 (같은 스레드에서)
            try { _hook?.Dispose(); } catch { }
            try { _monitor?.Dispose(); } catch { }
            try { _window?.Close(); } catch { }
        });
        thread.Name = "ToolHub.Clipboard";
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(15000))
            throw new TimeoutException("클립보드 모듈이 제시간에 시작되지 않았습니다.");
        if (failure != null)
        {
            thread.Join(2000);
            throw failure;
        }
        _thread = thread;
    }

    protected override void OnStop()
    {
        var dispatcher = _dispatcher;
        var thread = _thread;
        if (dispatcher != null) dispatcher.InvokeShutdown();
        thread?.Join(3000);

        _hook = null;
        _monitor = null;
        _window = null;
        _store = null;
        _dispatcher = null;
        _thread = null;
    }

    public override IList<ToolStripItem> CreateMenuItems()
    {
        var items = new List<ToolStripItem>();
        items.Add(new ToolStripMenuItem("열기 (Win+V)", null, (_, _) =>
            _dispatcher?.BeginInvoke(new Action(() => _window?.ShowPopup()))));
        items.Add(new ToolStripMenuItem("기록 모두 지우기", null, (_, _) =>
            _dispatcher?.BeginInvoke(new Action(() => _store?.Clear()))));
        var theme = new ToolStripMenuItem("테마");
        foreach (var (mode, label) in new[] { ("system", "시스템에 따름"), ("light", "라이트 모드"), ("dark", "다크 모드") })
        {
            var m = mode;
            theme.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) =>
            {
                Host.SetSetting(Id, "theme", m);
                _dispatcher?.BeginInvoke(new Action(() => { if (_window != null) _window.ThemeMode = m; }));
            }) { Checked = Host.GetSetting(Id, "theme", "system") == m });
        }
        items.Add(theme);
        return items;
    }

    /// <summary>
    /// --smoke 자체 점검: 목록 항목 템플릿으로 마우스 오버 툴팁을 만들어 텍스트/미리보기 바인딩이 값을 내는지 확인한다.
    /// </summary>
    public override string? SelfTest()
    {
        var dispatcher = _dispatcher;
        var window = _window;
        var store = _store;
        if (dispatcher == null || window == null || store == null) return "모듈이 실행 중이 아님";

        return (string)dispatcher.Invoke(new Func<string>(() =>
        {
            var sb = new System.Text.StringBuilder();
            foreach (bool wantImage in new[] { false, true })
            {
                ClipItem? item = null;
                foreach (var candidate in store.Items)
                    if (candidate.IsImage == wantImage) { item = candidate; break; }
                if (item == null) { sb.Append("[" + (wantImage ? "이미지" : "텍스트") + ": 항목 없음] "); continue; }

                var root = (System.Windows.FrameworkElement)window.List.ItemTemplate.LoadContent();
                root.DataContext = item;
                var tip = (System.Windows.Controls.ToolTip)root.ToolTip;
                tip.DataContext = item;

                // 실제 툴팁처럼 화면 밖(-20000,-20000)에 열어 바인딩이 활성화되게 한다.
                tip.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
                tip.HorizontalOffset = -20000;
                tip.VerticalOffset = -20000;
                tip.IsOpen = true;
                dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));
                var panel = (System.Windows.Controls.StackPanel)tip.Content;
                var image = (System.Windows.Controls.Image)panel.Children[0];
                var text = (System.Windows.Controls.TextBlock)panel.Children[1];
                text.GetBindingExpression(System.Windows.Controls.TextBlock.TextProperty)?.UpdateTarget();
                image.GetBindingExpression(System.Windows.Controls.Image.SourceProperty)?.UpdateTarget();
                image.GetBindingExpression(System.Windows.UIElement.VisibilityProperty)?.UpdateTarget();

                var be = text.GetBindingExpression(System.Windows.Controls.TextBlock.TextProperty);
                sb.Append("{binding=" + (be == null ? "null" : be.Status.ToString())
                    + ", ctx=" + (text.DataContext == null ? "null" : text.DataContext.GetType().Name)
                    + ", tipCtx=" + (tip.DataContext == null ? "null" : tip.DataContext.GetType().Name)
                    + ", fullText=" + item.FullText.Length + "} ");
                string src = image.Source is BitmapSource bs ? bs.PixelWidth + "x" + bs.PixelHeight : "없음";
                sb.Append("[" + (wantImage ? "이미지" : "텍스트") + ": 원문 "
                    + (wantImage ? (item.OcrText ?? "").Length : (item.Text ?? "").Length)
                    + "자 → 툴팁 " + text.Text.Length + "자, 미리보기=" + src
                    + ", 이미지표시=" + image.Visibility + "] ");
                tip.IsOpen = false;
            }
            return sb.ToString().Trim();
        }));
    }

    // ---- 이미지 알파 보정 (PicPick 등 알파 0 비트맵 대응) ------------------------

    /// <summary>
    /// 클립보드 이미지를 항상 같은 형태로 만든다: 96dpi, 알파가 전부 0 또는 전부 255 이면 불투명(Bgr32),
    /// 실제 투명도가 있으면 Bgra32.
    /// 앱마다(그리고 붙여넣기로 다시 복사될 때마다) 24/32bpp, DPI, 알파 0/255 가 달라서 같은 그림이
    /// 서로 다른 PNG 바이트(= 다른 해시)로 저장되어 붙여넣을 때마다 새 항목이 생기던 문제를 막는다.
    /// </summary>
    internal static BitmapSource NormalizeAlpha(BitmapSource src)
    {
        BitmapSource bgra = src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        byte first = pixels.Length > 3 ? pixels[3] : (byte)255;
        bool uniform = first == 0 || first == 255;
        for (int i = 3; uniform && i < pixels.Length; i += 4)
            if (pixels[i] != first) uniform = false;

        BitmapSource result;
        if (uniform)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        }
        else
        {
            result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        }
        result.Freeze();
        return result;
    }

    /// <summary>HistoryStore.AddImage 와 같은 방식으로 PNG 바이트를 만든다 (해시가 같아지도록).</summary>
    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource LoadPng(string path)
    {
        var decoder = new PngBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// 이미 저장된 캡처 중 알파가 0이라 투명하게 저장된 것을 한 번만 복구하고 OCR 을 다시 돌린다.
    /// (완료 표시: %AppData%\ClipboardApp\alpha-fixed-v1)
    /// </summary>
    private async void MaintainStoredImages()
    {
        CanonicalizeStoredImages();
        await RescanOcr();
    }

    /// <summary>OCR 이 확대 방식으로 바뀐 뒤 한 번만, 저장된 모든 이미지의 OCR 을 다시 돌린다. (표시: ocr-scale-v1)</summary>
    private async Task RescanOcr()
    {
        var store = _store;
        if (store == null) return;
        string marker = System.IO.Path.Combine(store.DataDir, "ocr-scale-v1");
        if (System.IO.File.Exists(marker)) return;

        try
        {
            int count = 0;
            foreach (var item in new List<ClipItem>(store.Items))
            {
                if (!item.IsImage || item.ImagePath is null || !System.IO.File.Exists(item.ImagePath)) continue;
                item.OcrText = await OcrService.RecognizeAsync(item.ImagePath);
                count++;
            }
            store.Save();
            System.IO.File.WriteAllText(marker, "");
            Host.Log(Id, "OCR 재인식: 이미지 " + count + "개");
        }
        catch (Exception ex)
        {
            Host.Log(Id, "OCR 재인식 중단: " + ex.Message);
        }
    }

    /// <summary>
    /// 이미 저장된 이미지를 새 정규화 형태로 다시 저장하고(알파 0 복구 포함), 같은 그림이 중복된 항목은
    /// 가장 최근 것만 남긴다. 한 번만 실행. (완료 표시: canonical-v1)
    /// </summary>
    private void CanonicalizeStoredImages()
    {
        var store = _store;
        if (store == null) return;
        string marker = System.IO.Path.Combine(store.DataDir, "canonical-v1");
        if (System.IO.File.Exists(marker)) return;

        try
        {
            string imageDir = System.IO.Path.Combine(store.DataDir, "images");
            var keepers = new Dictionary<string, ClipItem>();
            var plan = new List<(ClipItem Item, string Hash, byte[] Bytes)>();
            var duplicates = new List<ClipItem>();

            // Items 는 최신순이므로 처음 만난 것이 가장 최근 항목이다.
            foreach (var item in new List<ClipItem>(store.Items))
            {
                if (!item.IsImage || item.ImagePath is null || !System.IO.File.Exists(item.ImagePath)) continue;
                try
                {
                    var bytes = EncodePng(NormalizeAlpha(LoadPng(item.ImagePath)));
                    var hash = "I:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                    if (keepers.ContainsKey(hash)) { duplicates.Add(item); continue; }
                    keepers[hash] = item;
                    plan.Add((item, hash, bytes));
                }
                catch (Exception ex)
                {
                    Host.Log(Id, "저장된 이미지 정규화 실패: " + ex.Message);
                }
            }

            foreach (var dup in duplicates) store.Remove(dup, save: false);

            foreach (var (item, hash, bytes) in plan)
            {
                string name = hash.Substring(2, 16) + ".png";
                string path = System.IO.Path.Combine(imageDir, name);
                string? oldPath = item.ImagePath;
                System.IO.File.WriteAllBytes(path, bytes);
                if (oldPath != null && !string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    try { System.IO.File.Delete(oldPath); } catch (System.IO.IOException) { }
                }
                item.Hash = hash;
                item.ImageFile = name;
                item.ImagePath = path;
            }

            store.Save();
            System.IO.File.WriteAllText(marker, "");
            Host.Log(Id, "이미지 정규화: " + plan.Count + "개 유지, 중복 " + duplicates.Count + "개 제거");
        }
        catch (Exception ex)
        {
            Host.Log(Id, "이미지 정규화 중단: " + ex.Message);
        }
    }

    // ---- 원본 App.xaml.cs 의 클립보드 읽기 로직 ----------------------------------

    private async void OnClipboardChanged()
    {
        // 팝업에서 붙여넣으려고 우리가 직접 바꾼 클립보드는 다시 읽지 않는다(재인코딩/저장 낭비).
        if (_window is { } w && Environment.TickCount64 - w.LastPasteTick < 700) return;

        // The clipboard is often still locked by the copying app for a moment.
        await Task.Delay(40);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await ReadClipboard();
                return;
            }
            catch (COMException)
            {
                await Task.Delay(80);
            }
            catch (Exception ex)
            {
                // async void 에서 처리되지 않은 예외는 프로세스(허브) 전체를 종료시키므로 여기서 막는다.
                Host.Log(Id, "클립보드 읽기 실패: " + ex.Message);
                return;
            }
        }
    }

    private async Task ReadClipboard()
    {
        var store = _store;
        if (store == null) return;

        // Password managers flag their copies so they stay out of history.
        if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return;

        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            if (!string.IsNullOrWhiteSpace(text)) store.AddText(text);
        }
        else if (Clipboard.ContainsImage())
        {
            if (Clipboard.GetImage() is not BitmapSource img) return;
            // PicPick 등은 알파가 전부 0인 32bpp DIB 를 넣는다 -> 그대로 저장하면 투명 PNG 가 되어 미리보기/OCR 이 실패한다.
            var (item, isNew) = store.AddImage(NormalizeAlpha(img));
            if (isNew && item.ImagePath is not null)
            {
                var text = await OcrService.RecognizeAsync(item.ImagePath);
                item.OcrText = text;
                store.Save();
            }
        }
    }
}
