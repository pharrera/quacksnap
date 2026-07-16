using System.Runtime.InteropServices;
using QuackSnap.Core;

namespace QuackSnap.Capture;

/// <summary>
/// Event-driven clipboard watcher. AddClipboardFormatListener makes the OS post
/// WM_CLIPBOARDUPDATE to our hidden window — zero polling, zero idle CPU.
/// Must be created on the UI (STA) thread; the event fires on that thread.
/// </summary>
public sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    public event Action? ClipboardUpdated;

    public ClipboardMonitor()
    {
        CreateHandle(new CreateParams());
        if (!AddClipboardFormatListener(Handle))
            Logger.Error($"AddClipboardFormatListener failed: {Marshal.GetLastWin32Error()}");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            try { ClipboardUpdated?.Invoke(); }
            catch (Exception ex) { Logger.Error("Clipboard handler failed", ex); }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
