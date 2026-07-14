using ScreenDrop.Capture;
using ScreenDrop.Core;
using ScreenDrop.Transfer;

namespace ScreenDrop.UI;

/// <summary>
/// Small always-on-top window: drop images here to send them. Also shows live
/// transfer progress for anything moving through the queue.
/// </summary>
public sealed class DropForm : Form
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    private readonly Action<byte[], string, string> _enqueue;
    private readonly Label _hint;
    private readonly ListBox _activity = new();
    private readonly Dictionary<Guid, string> _lines = new();

    public DropForm(Action<byte[], string, string> enqueue, SendCoordinator coordinator)
    {
        _enqueue = enqueue;

        Text = "ScreenDrop";
        Size = new Size(340, 300);
        MinimumSize = new Size(280, 220);
        TopMost = true;
        AllowDrop = true;
        StartPosition = FormStartPosition.Manual;
        var screen = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(screen.Right - Width - 24, screen.Bottom - Height - 24);

        _hint = new Label
        {
            Text = "Drop images here\nto send to your paired device",
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 90,
            Font = new Font(Font.FontFamily, 10.5f),
        };
        _activity.Dock = DockStyle.Fill;
        _activity.IntegralHeight = false;
        _activity.SelectionMode = SelectionMode.None;
        _activity.BorderStyle = BorderStyle.None;

        Controls.Add(_activity);
        Controls.Add(_hint);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        coordinator.Progress += OnProgress;
        FormClosed += (_, _) => coordinator.Progress -= OnProgress;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        bool ok = e.Data != null &&
                  (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap));
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var file in files.Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
                {
                    var captured = ImageCapture.FromFile(file);
                    if (captured != null)
                        _enqueue(captured.PngBytes, Path.GetFileName(file), "image/png");
                }
            }
            else if (e.Data?.GetDataPresent(DataFormats.Bitmap) == true && e.Data.GetData(DataFormats.Bitmap) is Image image)
            {
                using var bmp = new Bitmap(image);
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                _enqueue(ms.ToArray(), $"Image {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png", "image/png");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Drag-drop handling failed", ex);
        }
    }

    private void OnProgress(TransferProgress p)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                string line = p switch
                {
                    { Done: true } => $"✓ {p.Name} → {p.DeviceName}",
                    { Failed: true } => $"✗ {p.Name} → {p.DeviceName} (will retry)",
                    _ => $"↑ {p.Name} → {p.DeviceName}  {(p.Total > 0 ? p.Sent * 100 / p.Total : 0)}%",
                };
                if (_lines.ContainsKey(p.ItemId))
                {
                    int index = _activity.Items.IndexOf(_lines[p.ItemId]);
                    if (index >= 0) _activity.Items[index] = line;
                    else _activity.Items.Add(line);
                }
                else
                {
                    _activity.Items.Add(line);
                }
                _lines[p.ItemId] = line;
                if (_activity.Items.Count > 50) _activity.Items.RemoveAt(0);
            });
        }
        catch (ObjectDisposedException) { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing hides; the tray owns the app lifetime.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}
