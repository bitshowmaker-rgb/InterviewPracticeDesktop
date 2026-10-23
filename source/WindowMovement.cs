using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MockInterviewAI
{
    public static class WindowMovement
    {
        // The window is a transparent, borderless layered window; the visible card sits
        // inside these transparent margins (see the root Grid margin in MainWindow.xaml).
        private const double CardLeft = 26, CardTop = 22, CardRight = 26, CardBottom = 18;
        private const double EdgeStrip = 9;       // Resizable hot strip just inside the card edge.
        private const double OuterStrip = 6;      // Resizable strip on the outermost transparent rim.

        public static void Attach(Window window)
        {
            window.PreviewMouseLeftButtonDown += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e) {
                var sourceElement = e.OriginalSource as DependencyObject;
                if (sourceElement == null || !IsBackground(sourceElement)) return;
                e.Handled = true;
                if (e.ClickCount == 2)
                    window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else
                {
                    try { window.DragMove(); }
                    catch (InvalidOperationException) { }
                }
            };
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source.AddHook(delegate(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
                if (message == 0x0083) // WM_NCCALCSIZE: keep default handling.
                    return IntPtr.Zero;
                if (message == 0x0024) // WM_GETMINMAXINFO: constrain maximized state to the work area.
                {
                    IntPtr monitor = MonitorFromWindow(hwnd, 0x00000002 /* MONITOR_DEFAULTTONEAREST */);
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                        mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
                        mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;
                        mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
                        mmi.ptMaxSize.y = info.rcWork.bottom - info.rcMonitor.top;
                        mmi.ptMinTrackSize.x = (int)Math.Ceiling(window.MinWidth);
                        mmi.ptMinTrackSize.y = (int)Math.Ceiling(window.MinHeight);
                        Marshal.StructureToPtr(mmi, lParam, true);
                        handled = true;
                    }
                    return IntPtr.Zero;
                }
                if (message != 0x0084) return IntPtr.Zero; // WM_NCHITTEST
                long coordinates = lParam.ToInt64();
                var screen = new Point((short)(coordinates & 0xffff), (short)((coordinates >> 16) & 0xffff));
                Point point = window.PointFromScreen(screen);
                if (point.X < 0 || point.Y < 0 || point.X >= window.ActualWidth || point.Y >= window.ActualHeight) return IntPtr.Zero;
                int edge = ResizeEdge(window, point);
                if (edge != 0) { handled = true; return new IntPtr(edge); }
                return IntPtr.Zero;
            });
        }

        private static int ResizeEdge(Window window, Point p)
        {
            if (window.WindowState != WindowState.Normal) return 0;
            double w = window.ActualWidth, h = window.ActualHeight;
            bool left = (p.X >= CardLeft && p.X < CardLeft + EdgeStrip) || p.X < OuterStrip;
            bool right = (p.X >= w - CardRight - EdgeStrip && p.X < w - CardRight) || p.X >= w - OuterStrip;
            bool top = (p.Y >= CardTop && p.Y < CardTop + EdgeStrip) || p.Y < OuterStrip;
            bool bottom = (p.Y >= h - CardBottom - EdgeStrip && p.Y < h - CardBottom) || p.Y >= h - OuterStrip;
            // Visible resize grip in the bottom-right corner of the card.
            if (!bottom && !right && p.X >= w - 52 && p.X <= w - 22 && p.Y >= h - 44 && p.Y <= h - 14)
                return 17; // HTBOTTOMRIGHT
            if (top) return left ? 13 : right ? 14 : 12;
            if (bottom) return left ? 16 : right ? 17 : 15;
            return left ? 10 : right ? 11 : 0;
        }

        private static bool IsBackground(DependencyObject item)
        {
            while (item != null)
            {
                if (item is ButtonBase || item is TextBoxBase || item is PasswordBox || item is RangeBase || item is ScrollViewer || item is ResizeGrip || item is Hyperlink) return false;
                var content = item as FrameworkContentElement;
                if (content != null) item = content.Parent;
                else if (item is Visual || item is Visual3D) item = VisualTreeHelper.GetParent(item);
                else return true;
            }
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    }
}
