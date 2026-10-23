using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MockInterviewAI
{
    public sealed class CaptureState
    {
        public bool Applied { get; set; }
        public bool Verified { get; set; }
        public uint Affinity { get; set; }
        public int ErrorCode { get; set; }
    }

    public static class CaptureProtection
    {
        public const uint ExcludeFromCapture = 0x11;
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

        public static CaptureState Apply(IntPtr window)
        {
            bool applied = SetWindowDisplayAffinity(window, ExcludeFromCapture);
            int error = applied ? 0 : Marshal.GetLastWin32Error();
            uint affinity;
            bool read = GetWindowDisplayAffinity(window, out affinity);
            return new CaptureState { Applied = applied, Verified = read && affinity == ExcludeFromCapture, Affinity = affinity, ErrorCode = error };
        }
    }
}
