using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ToolHub.Modules.PowerPoint
{
    // UI Automation is deliberately kept off the low-level mouse hook thread.
    internal sealed class SlideScrollController : IDisposable
    {
        private sealed class Target
        {
            internal IntPtr Root;
            internal System.Windows.Rect Bounds;
        }
        private readonly Control _dispatch = new Control();
        private readonly System.Windows.Forms.Timer _timer;
        private volatile Target _target;
        private AutomationElement _pane;
        private ScrollPattern _scroll;
        private IntPtr _root;
        private readonly object _sync = new object();
        private int _pending;
        private IntPtr _pendingRoot;
        private bool _queued;
        private volatile bool _disposed;
        private DateTime _lastLog;

        internal SlideScrollController()
        {
            IntPtr handle = _dispatch.Handle;
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 200;
            _timer.Tick += delegate { Refresh(); };
            _timer.Start();
        }

        private void Refresh()
        {
            try
            {
                IntPtr root = NativeMethods.GetForegroundWindow();
                if (!NativeMethods.GetWindowClassName(root).Equals("PPTFrameClass", StringComparison.OrdinalIgnoreCase))
                { _target = null; return; }
                if (_root != root || _pane == null)
                {
                    uint pid;
                    NativeMethods.GetWindowThreadProcessId(root, out pid);
                    using (var process = Process.GetProcessById((int)pid))
                        if (!process.ProcessName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase))
                        { _target = null; return; }
                    var frame = AutomationElement.FromHandle(root);
                    _pane = frame.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "SlidePane"));
                    _root = root;
                    _scroll = _pane == null ? null : _pane.GetCurrentPattern(ScrollPattern.Pattern) as ScrollPattern;
                }
                if (_pane == null || _scroll == null || _pane.Current.IsOffscreen || !_scroll.Current.HorizontallyScrollable)
                { _target = null; return; }
                _target = new Target { Root = root, Bounds = _pane.Current.BoundingRectangle };
            }
            catch (Exception ex)
            {
                _target = null; _pane = null; _scroll = null;
                Report("SlidePane lookup: " + ex.Message);
            }
        }

        internal bool QueueScroll(int direction, int step)
        {
            Target target = _target;
            if (_disposed || target == null || target.Root != NativeMethods.GetForegroundWindow()) return false;
            NativeMethods.Point point;
            if (!NativeMethods.GetCursorPos(out point) || !target.Bounds.Contains(point.X, point.Y)) return false;
            if (GetAncestor(NativeMethods.WindowFromPoint(point), 2) != target.Root) return false;
            if (direction == 0) return true;
            lock (_sync)
            {
                if (_pendingRoot != target.Root) _pending = 0;
                _pendingRoot = target.Root;
                _pending = Math.Max(-30, Math.Min(30, _pending + direction * step));
                if (!_queued)
                {
                    _queued = true;
                    try { _dispatch.BeginInvoke(new Action(Drain)); }
                    catch { _queued = false; _pending = 0; return false; }
                }
            }
            return true;
        }

        private void Drain()
        {
            int count;
            IntPtr root;
            lock (_sync) { count = _pending; root = _pendingRoot; _pending = 0; _queued = false; }
            if (_disposed || count == 0 || root != NativeMethods.GetForegroundWindow() || root != _root) return;
            try
            {
                if (_scroll == null || !_scroll.Current.HorizontallyScrollable) return;
                double before = _scroll.Current.HorizontalScrollPercent;
                for (int i = 0; i < Math.Abs(count); i++)
                    _scroll.Scroll(count > 0 ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement,
                        ScrollAmount.NoAmount);
                Report("Shift-wheel: " + before.ToString("F2") + "% -> " +
                    _scroll.Current.HorizontalScrollPercent.ToString("F2") + "%");
            }
            catch (Exception ex)
            {
                _target = null; _pane = null; _scroll = null;
                Report("SlidePane scroll: " + ex.Message);
            }
        }

        private void Report(string message)
        {
            if ((DateTime.UtcNow - _lastLog).TotalSeconds < 2) return;
            _lastLog = DateTime.UtcNow;
            AppLog.Write(message);
        }

        public void Dispose()
        {
            _disposed = true;
            _target = null;
            _timer.Stop(); _timer.Dispose(); _dispatch.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    }
}
