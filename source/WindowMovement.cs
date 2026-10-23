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
        public static string LastDragDiagnostic { get; private set; }
        public static void Attach(Window window)
        {
            window.PreviewMouseLeftButtonDown += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e) {
                var sourceElement = e.OriginalSource as DependencyObject;
                LastDragDiagnostic = "MouseDown=" + (sourceElement == null ? "null" : sourceElement.GetType().Name) + "; left=" + e.LeftButton + "; background=" + (sourceElement != null && IsBackground(sourceElement));
                if (sourceElement == null || !IsBackground(sourceElement)) return;
                e.Handled = true;
                if (e.ClickCount == 2)
                    window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else
                {
                    try { window.DragMove(); LastDragDiagnostic += "; DragMove returned"; }
                    catch (InvalidOperationException error) { LastDragDiagnostic += "; " + error.Message; }
                }
            };
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source.AddHook(delegate(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
                if (message != 0x84) return IntPtr.Zero;
                long coordinates = lParam.ToInt64();
                var screen = new Point((short)(coordinates & 0xffff), (short)((coordinates >> 16) & 0xffff));
                Point point = window.PointFromScreen(screen);
                if (point.X < 0 || point.Y < 0 || point.X >= window.ActualWidth || point.Y >= window.ActualHeight) return IntPtr.Zero;
                int edge = ResizeEdge(window, point);
                if (edge != 0) { handled = true; return new IntPtr(edge); }
                return IntPtr.Zero;
            });
        }

        public static bool CanStartDrag(Window window, FrameworkElement element)
        {
            Point position = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), window);
            var hit = window.InputHitTest(position) as DependencyObject;
            return hit != null && IsBackground(hit);
        }

        private static int ResizeEdge(Window window, Point p)
        {
            if (window.WindowState != WindowState.Normal || (window.ResizeMode != ResizeMode.CanResize && window.ResizeMode != ResizeMode.CanResizeWithGrip)) return 0;
            bool left = p.X < 7, right = p.X >= window.ActualWidth - 7;
            bool top = p.Y < 7, bottom = p.Y >= window.ActualHeight - 7;
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

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        public static int ReadHitTest(Window window, FrameworkElement element)
        {
            Point position = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            long coordinates = ((long)(ushort)(short)Math.Round(position.Y) << 16) | (ushort)(short)Math.Round(position.X);
            return SendMessage(new WindowInteropHelper(window).Handle, 0x84, IntPtr.Zero, new IntPtr(coordinates)).ToInt32();
        }
    }
}
