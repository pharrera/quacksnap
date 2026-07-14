using Microsoft.Win32;
using ScreenDrop.Capture;
using ScreenDrop.Core;
using ScreenDrop.Transfer;

namespace ScreenDrop.UI;

/// <summary>
/// The resident shell: tray icon, menu, and the wiring between capture events and
/// the transfer queue. No visible window exists unless the user opens one.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ScreenDrop";

    private readonly Identity _identity;
    private readonly StateStore _store;
    private readonly TransferQueue _queue;
    private readonly SendCoordinator _coordinator;
    private readonly ClipboardMonitor _clipboard;
    private readonly ScreenshotFolderWatcher _folderWatcher;
    private readonly Deduper _deduper = new();
    private readonly NotifyIcon _tray;
    private readonly Control _uiThread = new();
    private DropForm? _dropForm;
    private PairingForm? _pairingForm;

    public TrayApplicationContext(Identity identity, StateStore store, TransferQueue queue, SendCoordinator coordinator)
    {
        _identity = identity;
        _store = store;
        _queue = queue;
        _coordinator = coordinator;
        _ = _uiThread.Handle; // force handle creation on the UI thread for marshaling

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "ScreenDrop",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Opening += (_, _) => RebuildMenu();
        _tray.DoubleClick += (_, _) => ShowDropWindow();
        RebuildMenu();

        _clipboard = new ClipboardMonitor();
        _clipboard.ClipboardUpdated += OnClipboardUpdated;

        _folderWatcher = new ScreenshotFolderWatcher();
        _folderWatcher.ScreenshotSaved += path =>
        {
            var captured = ImageCapture.FromFile(path);
            if (captured != null)
                EnqueueCaptured(captured, Path.GetFileName(path));
        };

        _coordinator.ReachabilityChanged += () => RunOnUiThread(UpdateTrayText);
        _queue.Changed += () => RunOnUiThread(UpdateTrayText);
        _ = _coordinator.RefreshReachabilityAsync();

        Logger.Info("Tray started");
    }

    // ---- capture pipeline ----

    private void OnClipboardUpdated()
    {
        var settings = _store.State.Settings;
        if (!settings.AutoSendScreenshots) return;
        if (!settings.SendAllCopiedImages && !ScreenshotDetector.LooksLikeScreenshot()) return;

        var captured = ImageCapture.FromClipboard();
        if (captured == null) return;
        EnqueueCaptured(captured, $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
    }

    private void EnqueueCaptured(CapturedImage captured, string name)
    {
        if (!_deduper.IsNew(captured.PixelHash))
        {
            Logger.Info($"Duplicate capture skipped ({name})");
            return;
        }
        EnqueueBytes(captured.PngBytes, name, "image/png");
    }

    /// <summary>Also used by the drop window for dragged-in images.</summary>
    public void EnqueueBytes(byte[] payload, string name, string mime)
    {
        var devices = _store.State.Devices;
        if (devices.Count == 0)
        {
            Logger.Info("Capture ignored — no paired devices");
            return;
        }
        _queue.Enqueue(payload, name, mime, devices);
        Logger.Info($"Queued {name} ({payload.Length / 1024} KB) for {devices.Count} device(s)");
    }

    // ---- tray UI ----

    private void RebuildMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();

        if (_store.State.Devices.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("No paired devices") { Enabled = false });
        }
        else
        {
            foreach (var device in _store.State.Devices)
            {
                string dot = _coordinator.IsReachable(device.Id) ? "●" : "○";
                var item = new ToolStripMenuItem($"{dot} {device.Name}") { Enabled = false };
                menu.Items.Add(item);
            }
            _ = _coordinator.RefreshReachabilityAsync(); // refresh for next open
        }
        menu.Items.Add(new ToolStripSeparator());

        var autoSend = new ToolStripMenuItem("Auto-send screenshots") { Checked = _store.State.Settings.AutoSendScreenshots, CheckOnClick = true };
        autoSend.CheckedChanged += (_, _) => _store.Mutate(s => s.Settings.AutoSendScreenshots = autoSend.Checked);
        menu.Items.Add(autoSend);

        var allImages = new ToolStripMenuItem("Send all copied images") { Checked = _store.State.Settings.SendAllCopiedImages, CheckOnClick = true };
        allImages.CheckedChanged += (_, _) => _store.Mutate(s => s.Settings.SendAllCopiedImages = allImages.Checked);
        menu.Items.Add(allImages);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open drop window", null, (_, _) => ShowDropWindow());

        var pair = new ToolStripMenuItem("Pair a device");
        pair.DropDownItems.Add("Pair application…", null, (_, _) => ShowPairingWindow());
        pair.DropDownItems.Add(new ToolStripMenuItem("Pair website… (coming soon)") { Enabled = false });
        menu.Items.Add(pair);

        if (_store.State.Devices.Count > 0)
        {
            var unpair = new ToolStripMenuItem("Unpair");
            foreach (var device in _store.State.Devices)
            {
                var d = device;
                unpair.DropDownItems.Add(d.Name, null, (_, _) =>
                {
                    _queue.DropForDevice(d.Id);
                    _store.Mutate(s => s.Devices.RemoveAll(x => x.Id == d.Id));
                });
            }
            menu.Items.Add(unpair);
        }

        var recents = new ToolStripMenuItem("Recent");
        if (_store.State.Recents.Count == 0)
            recents.DropDownItems.Add(new ToolStripMenuItem("Nothing sent yet") { Enabled = false });
        foreach (var r in _store.State.Recents)
            recents.DropDownItems.Add(new ToolStripMenuItem($"{r.Name} → {r.DeviceName} ({r.Status}, {r.At.ToLocalTime():HH:mm})") { Enabled = false });
        menu.Items.Add(recents);

        menu.Items.Add(new ToolStripSeparator());

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartupEnabled(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add("Quit", null, (_, _) => ExitThread());
    }

    private void UpdateTrayText()
    {
        int pending = _queue.PendingCount;
        _tray.Text = pending == 0 ? "ScreenDrop" : $"ScreenDrop — {pending} pending";
    }

    private void ShowDropWindow()
    {
        if (_dropForm == null || _dropForm.IsDisposed)
            _dropForm = new DropForm((bytes, name, mime) => EnqueueBytes(bytes, name, mime), _coordinator);
        _dropForm.Show();
        _dropForm.Activate();
    }

    private void ShowPairingWindow()
    {
        if (_pairingForm == null || _pairingForm.IsDisposed)
            _pairingForm = new PairingForm(_identity, _store);
        _pairingForm.Show();
        _pairingForm.Activate();
    }

    private void RunOnUiThread(Action action)
    {
        try
        {
            if (_uiThread.IsHandleCreated) _uiThread.BeginInvoke(action);
        }
        catch (ObjectDisposedException) { }
    }

    // ---- startup registration ----

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
        Logger.Info($"Start with Windows: {enabled}");
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _clipboard.Dispose();
        _folderWatcher.Dispose();
        _coordinator.Dispose();
        base.ExitThreadCore();
    }
}
