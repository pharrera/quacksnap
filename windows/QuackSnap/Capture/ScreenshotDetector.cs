using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QuackSnap.Capture;

/// <summary>
/// Decides whether the image currently on the clipboard came from a screenshot
/// tool, by looking at which process owns the clipboard.
/// </summary>
public static class ScreenshotDetector
{
    private static readonly HashSet<string> ScreenshotOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "SnippingTool",        // Windows 11 Snipping Tool / Win+Shift+S
        "ScreenClippingHost",  // Windows 10 snip overlay
        "ScreenSketch",        // Snip & Sketch
        "ShellExperienceHost",
    };

    public static bool LooksLikeScreenshot()
    {
        string? owner = ClipboardOwnerProcessName();
        // PrintScreen has no owner window; screenshot tools are in the known set.
        return owner is null || ScreenshotOwners.Contains(owner);
    }

    public static string? ClipboardOwnerProcessName()
    {
        IntPtr hwnd = GetClipboardOwner();
        if (hwnd == IntPtr.Zero) return null;
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
