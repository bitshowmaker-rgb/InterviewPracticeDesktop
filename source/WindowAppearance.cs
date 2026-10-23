using System;
using System.Runtime.InteropServices;
namespace MockInterviewAI {
 public static class WindowAppearance {
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")]
  private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
  public static bool IsLayered(IntPtr window) { return window!=IntPtr.Zero && (GetWindowLongPtr(window,-20).ToInt64() & 0x80000L)!=0; }
 }
}
