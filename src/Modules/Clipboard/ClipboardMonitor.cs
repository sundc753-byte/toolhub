using System;
using System.Windows.Interop;

namespace ClipboardApp;

/// <summary>Raises <see cref="Changed"/> whenever the system clipboard content changes.</summary>
internal sealed class ClipboardMonitor : IDisposable
{
    private const int HWND_MESSAGE = -3;
    private readonly HwndSource _source;

    public event Action? Changed;

    public ClipboardMonitor()
    {
        var p = new HwndSourceParameters("ClipboardAppMonitor")
        {
            ParentWindow = new IntPtr(HWND_MESSAGE),
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        Native.AddClipboardFormatListener(_source.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
            Changed?.Invoke();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Native.RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }
}
