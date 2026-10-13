using System.Globalization;
using Microsoft.Win32;

namespace ToolHub.Modules.Timestamp;

/// <summary>A selectable timestamp style.</summary>
internal sealed record StampFormat(string Name, Func<DateTimeOffset, string> Render);

/// <summary>
/// Win + ; 로 현재 시각을 포커스된 창에 입력한다.
/// 원본: github.com/sundc753-byte/timestamp (TimestampTray). 트레이/자동 시작은 허브가 담당하므로 제외했다.
/// </summary>
public sealed class TimestampModule : ModuleBase
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly StampFormat[] Formats =
    {
        new("yyyy-MM-dd HH:mm:ss",      t => t.ToString("yyyy-MM-dd HH:mm:ss", Inv)),
        new("yyyy-MM-dd",               t => t.ToString("yyyy-MM-dd", Inv)),
        new("HH:mm:ss",                 t => t.ToString("HH:mm:ss", Inv)),
        new("yyyy-MM-dd HH:mm:ss.fff",  t => t.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv)),
        new("yyyyMMdd_HHmmss",          t => t.ToString("yyyyMMdd_HHmmss", Inv)),
        new("yyyy.MM.dd (ddd) HH:mm",   t => t.ToString("yyyy.MM.dd (ddd) HH:mm", CultureInfo.CurrentCulture)),
        new("ISO 8601",                 t => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv)),
        new("Unix (초)",                 t => t.ToUnixTimeSeconds().ToString(Inv)),
        new("Unix (밀리초)",              t => t.ToUnixTimeMilliseconds().ToString(Inv)),
    };

    private KeyboardHook? _hook;
    private volatile int _selected;

    public override string Id => "timestamp";
    public override string DisplayName => "타임스탬프 (Win+;)";
    public override string Description => "Win + ; 로 현재 시각을 포커스된 창에 입력 (형식은 트레이 메뉴에서 선택)";
    public override string[] ConflictProcessNames => new[] { "TimestampTray" };
    public override string[] LegacyRunValueNames => new[] { "TimestampTray" };

    protected override void OnStart()
    {
        _selected = LoadSelected();

        var hook = new KeyboardHook();
        hook.WinSemicolonPressed += () => ThreadPool.QueueUserWorkItem(_ => Stamp());
        hook.Install();
        _hook = hook;
    }

    protected override void OnStop()
    {
        _hook?.Dispose();
        _hook = null;
    }

    private void Stamp()
    {
        string text = Formats[_selected].Render(DateTimeOffset.Now);
        try { KeyboardHook.TypeText(text); }
        catch (Exception ex) { Host.Log(Id, "입력 실패: " + ex.Message); }
    }

    public override IList<ToolStripItem> CreateMenuItems()
    {
        var items = new List<ToolStripItem>();
        var now = DateTimeOffset.Now;
        for (int i = 0; i < Formats.Length; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem($"{Formats[i].Name}    →  {Formats[i].Render(now)}")
            {
                Checked = i == _selected,
            };
            item.Click += (_, _) =>
            {
                _selected = index;
                Host.SetSetting(Id, "format", index.ToString());
            };
            items.Add(item);
        }
        return items;
    }

    private int LoadSelected()
    {
        if (int.TryParse(Host.GetSetting(Id, "format", null!), out int saved) && saved >= 0 && saved < Formats.Length)
            return saved;

        // 기존 단독 프로그램이 레지스트리에 저장해 둔 형식을 처음 한 번 이어받는다.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\TimestampTray");
            if (key?.GetValue("Format") is int legacy && legacy >= 0 && legacy < Formats.Length)
                return legacy;
        }
        catch { }
        return 0;
    }
}
