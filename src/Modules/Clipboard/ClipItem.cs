using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace ClipboardApp;

public sealed class ClipItem : INotifyPropertyChanged
{
    public string Hash { get; set; } = "";
    public bool IsImage { get; set; }
    public string? Text { get; set; }
    public string? ImageFile { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;

    private string? _ocrText;
    public string? OcrText
    {
        get => _ocrText;
        set
        {
            _ocrText = value;
            Raise(nameof(OcrText));
            Raise(nameof(Subtitle));
            Raise(nameof(FullText));
        }
    }

    /// <summary>Absolute path of the saved PNG; filled in by <see cref="HistoryStore"/>.</summary>
    [JsonIgnore] public string? ImagePath { get; set; }

    [JsonIgnore]
    public string Title => IsImage
        ? $"이미지 {Width}×{Height}"
        : Truncate(Text ?? "", 300);

    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            if (!IsImage) return $"{(Text ?? "").Length}자";
            if (OcrText is null) return "텍스트 인식 중…";
            var t = OcrText.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return t.Length == 0 ? "인식된 텍스트 없음" : "🔍 " + Truncate(t, 120);
        }
    }

    private string _number = "";

    /// <summary>Hotkey label shown beside the item ("1".."9","0", or blank); set by the window.</summary>
    [JsonIgnore]
    public string Number
    {
        get => _number;
        set
        {
            if (_number == value) return;
            _number = value;
            Raise(nameof(Number));
        }
    }

    private BitmapImage? _thumb;

    [JsonIgnore]
    public BitmapImage? Thumbnail
    {
        get
        {
            if (_thumb is null && ImagePath is not null && File.Exists(ImagePath))
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(ImagePath);
                b.DecodePixelWidth = 160;
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.EndInit();
                b.Freeze();
                _thumb = b;
            }
            return _thumb;
        }
    }

    // [ToolHub] 마우스 오버 툴팁용 -------------------------------------------------

    private const int TooltipMaxChars = 5000;

    /// <summary>툴팁에 보여줄 전체 내용: 텍스트는 전문, 이미지는 OCR 로 인식한 텍스트.</summary>
    [JsonIgnore]
    public string FullText
    {
        get
        {
            string s = IsImage
                ? (OcrText is null ? "텍스트 인식 중…" : (OcrText.Trim().Length == 0 ? "인식된 텍스트 없음" : OcrText.Trim()))
                : (Text ?? "");
            return s.Length <= TooltipMaxChars ? s : s[..TooltipMaxChars] + $"\n…(이하 생략, 총 {s.Length}자)";
        }
    }

    private BitmapImage? _preview;

    /// <summary>툴팁용 큰 미리보기(가로 520px 로 디코딩). 파일을 잠그지 않도록 OnLoad 로 읽는다.</summary>
    [JsonIgnore]
    public BitmapImage? Preview
    {
        get
        {
            if (_preview is null && IsImage && ImagePath is not null && File.Exists(ImagePath))
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(ImagePath);
                b.DecodePixelWidth = 520;
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.EndInit();
                b.Freeze();
                _preview = b;
            }
            return _preview;
        }
    }

    public bool Matches(string query) =>
        (Text?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        (OcrText?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
