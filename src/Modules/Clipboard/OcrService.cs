using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ClipboardApp;

/// <summary>Text recognition using the OCR engine built into Windows 10/11.</summary>
internal static class OcrService
{
    private static OcrEngine? _engine;
    private static bool _initialized;

    private static OcrEngine? Engine()
    {
        if (_initialized) return _engine;
        _initialized = true;
        _engine = OcrEngine.TryCreateFromUserProfileLanguages();
        foreach (var tag in new[] { "ko", "en-US" })
        {
            if (_engine is not null) break;
            _engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
        }
        return _engine;
    }

    /// <summary>Returns recognized text, or an empty string when nothing/no engine is available.</summary>
    public static async Task<string> RecognizeAsync(string pngPath)
    {
        try
        {
            var engine = Engine();
            if (engine is null) return "";

            using var stream = File.OpenRead(pngPath);
            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());

            // [ToolHub] 화면 캡처의 작은 글자는 그대로는 인식률이 낮아, OCR 용 임시 이미지만 최대 3배로 키운다.
            // (저장된 원본 이미지는 바뀌지 않는다.)
            double longest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            double scale = Math.Min(3.0, OcrEngine.MaxImageDimension / longest);
            if (scale < 1) return "";

            var transform = new BitmapTransform();
            if (scale > 1.05)
            {
                transform.ScaledWidth = (uint)Math.Round(decoder.PixelWidth * scale);
                transform.ScaledHeight = (uint)Math.Round(decoder.PixelHeight * scale);
                transform.InterpolationMode = BitmapInterpolationMode.Cubic;
            }

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

            if (bitmap.PixelWidth > OcrEngine.MaxImageDimension || bitmap.PixelHeight > OcrEngine.MaxImageDimension)
                return "";

            var result = await engine.RecognizeAsync(bitmap);
            return result.Text.Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }
}
