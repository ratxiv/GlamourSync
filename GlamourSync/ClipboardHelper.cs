using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GlamourSync;

// Reads the Windows clipboard directly (no ImGui needed), so it also works from chat commands.
// Besides plain text it can read the "HTML Format" a browser puts there on Ctrl+C.
internal static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private static readonly uint CfHtml = RegisterClipboardFormat("HTML Format");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr hMem);

    // Changes every time something new is copied; lets us skip re-reading an unchanged clipboard.
    public static uint SequenceNumber() => GetClipboardSequenceNumber();

    public static string? GetText() => Read(CF_UNICODETEXT, (ptr, _) => Marshal.PtrToStringUni(ptr));

    
    public static string? GetHtml()
    {
        if (CfHtml == 0) return null;
        return Read(CfHtml, (ptr, size) =>
        {
            var length = (int)Math.Min((ulong)size, int.MaxValue);
            var bytes = new byte[length];
            Marshal.Copy(ptr, bytes, 0, length);
            var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            var start = text.IndexOf('<');
            return start >= 0 ? text[start..] : null;
        });
    }

    private static string? Read(uint format, Func<IntPtr, ulong, string?> reader)
    {
        // Another program may be holding the clipboard for a moment
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var handle = GetClipboardData(format);
                    if (handle == IntPtr.Zero) return null;

                    var size = (ulong)GlobalSize(handle);
                    var ptr = GlobalLock(handle);
                    if (ptr == IntPtr.Zero) return null;
                    try { return reader(ptr, size); }
                    finally { GlobalUnlock(handle); }
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(20);
        }
        return null;
    }
}