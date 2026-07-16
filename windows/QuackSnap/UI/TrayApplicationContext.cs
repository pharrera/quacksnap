using Microsoft.Win32;
using QuackSnap.Capture;
using QuackSnap.Core;
using QuackSnap.Transfer;

namespace QuackSnap.UI;

/// <summary>
/// The resident shell: tray icon, menu, and the wiring between capture events and
/// the transfer queue. No visible window exists unless the user opens one.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "QuackSnap";

    private readonly Identity _identity;
    private readonly StateStore _store;
    private readonly TransferQueue _queue;
    private readonly SendCoordinator _coordinator;
    private readonly ClipboardMonitor _clipboard;
    private readonly ScreenshotFolderWatcher _folderWatcher;
    private readonly ReceiveServer _receiveServer;
    private readonly Deduper _deduper = new();
    private readonly NotifyIcon _tray;
    private readonly Control _uiThread = new();
    private DropForm? _dropForm;
    private PairingForm? _pairingForm;

    public TrayApplicationContext(Identity identity, StateStore store, TransferQueue queue, SendCoordinator coordinator, ReceiveServer receiveServer)
    {
        _identity = identity;
        _store = store;
        _queue = queue;
        _coordinator = coordinator;
        _receiveServer = receiveServer;
        _ = _uiThread.Handle; // force handle creation on the UI thread for marshaling

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "QuackSnap",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Opening += (_, _) => RebuildMenu();
        _tray.DoubleClick += (_, _) => ShowDropWindow();
        _tray.BalloonTipClicked += (_, _) => OpenReceiveFolder();
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
        _receiveServer.FileReceived += OnFileReceived;
        _ = _coordinator.RefreshReachabilityAsync();

        // M3 onboarding: the inbound pairing/transfer connections trip Windows
        // Firewall the first time — tell the user what to click before it happens.
        if (!_store.State.Settings.FirstRunTipShown)
        {
            _tray.ShowBalloonTip(10000, "QuackSnap is running",
                "Take a screenshot and it goes to your paired phone. If Windows Firewall asks, choose Allow so your phone can connect.",
                ToolTipIcon.Info);
            _store.Mutate(s => s.Settings.FirstRunTipShown = true);
        }

        Logger.Info("Tray started");
    }

    // ---- capture pipeline ----

    private void OnClipboardUpdated()
    {
        var settings = _store.State.Settings;

        // Image path: screenshots (or any copied image if that's enabled).
        if (settings.AutoSendScreenshots &&
            (settings.SendAllCopiedImages || ScreenshotDetector.LooksLikeScreenshot()))
        {
            var captured = ImageCapture.FromClipboard();
            if (captured != null)
            {
                EnqueueCaptured(captured, $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
                return;
            }
        }

        // Text path: copied/highlighted text, sent as a .txt so it lands on the
        // phone's clipboard ready to paste. Off by default — every Ctrl+C would
        // otherwise be transferred.
        if (settings.SendCopiedText)
            TrySendClipboardText();
    }

    private void TrySendClipboardText()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            string text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > 1_000_000) return; // sanity cap; text is normally tiny

            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            string hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes));
            if (!_deduper.IsNew(hash))
            {
                Logger.Info("Duplicate copied text skipped");
                return;
            }
            EnqueueBytes(bytes, $"Text {DateTime.Now:yyyy-MM-dd HH.mm.ss}.txt", "text/plain");
        }
        catch (Exception ex)
        {
            Logger.Error("Sending copied text failed", ex);
        }
    }

    private void EnqueueCaptured(CapturedImage captured, string name)
    {
        if (!_deduper.IsNew(captured.PixelHash))
        {
            Logger.Info($"Duplicate capture skipped ({name})");
            return;
        }
        if (_store.State.Settings.CompressScreenshots)
        {
            try
            {
                var jpeg = ImageCapture.ToJpeg(captured.PngBytes);
                EnqueueBytes(jpeg, Path.ChangeExtension(name, ".jpg"), "image/jpeg");
                return;
            }
            catch (Exception ex)
            {
                Logger.Error("JPEG compression failed, sending PNG", ex);
            }
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

    // ---- reverse direction: files arriving from the phone ----

    private void OnFileReceived(ReceivedFile file)
    {
        _store.AddRecent(file.Name, file.From, "received");
        RunOnUiThread(() =>
        {
            _tray.ShowBalloonTip(6000, $"Received from {file.From}",
                $"{file.Name} — click to open the folder.", ToolTipIcon.Info);
        });
    }

    private void OpenReceiveFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _receiveServer.ReceiveDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Opening receive folder failed", ex);
        }
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

        var copiedText = new ToolStripMenuItem("Send copied text") { Checked = _store.State.Settings.SendCopiedText, CheckOnClick = true };
        copiedText.CheckedChanged += (_, _) => _store.Mutate(s => s.Settings.SendCopiedText = copiedText.Checked);
        menu.Items.Add(copiedText);

        var compress = new ToolStripMenuItem("Compress screenshots (JPEG)") { Checked = _store.State.Settings.CompressScreenshots, CheckOnClick = true };
        compress.CheckedChanged += (_, _) => _store.Mutate(s => s.Settings.CompressScreenshots = compress.Checked);
        menu.Items.Add(compress);

        string relayLabel = string.IsNullOrWhiteSpace(_store.State.Settings.RelayUrl)
            ? "Set relay URL… (background delivery)"
            : $"Relay: {_store.State.Settings.RelayUrl}";
        menu.Items.Add(relayLabel, null, (_, _) => PromptRelayUrl());

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open drop window", null, (_, _) => ShowDropWindow());
        menu.Items.Add("Open received files", null, (_, _) => OpenReceiveFolder());

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

    private static Icon LoadAppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    private void UpdateTrayText()
    {
        int pending = _queue.PendingCount;
        _tray.Text = pending == 0 ? "QuackSnap" : $"QuackSnap — {pending} pending";
    }

    private void ShowDropWindow()
    {
        if (_dropForm == null || _dropForm.IsDisposed)
            _dropForm = new DropForm((bytes, name, mime) => EnqueueBytes(bytes, name, mime), _coordinator);
        _dropForm.Show();
        _dropForm.Activate();
    }

    private void PromptRelayUrl()
    {
        using var dialog = new Form
        {
            Text = "Relay URL — QuackSnap",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(420, 130),
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
        };
        var label = new Label
        {
            Text = "Deliver files through this E2EE relay when the phone is away (blank = LAN only):",
            Location = new Point(12, 10),
            Size = new Size(396, 34),
        };
        var box = new TextBox { Location = new Point(12, 50), Width = 396, Text = _store.State.Settings.RelayUrl ?? "" };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(252, 88), Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(333, 88), Width = 75 };
        dialog.Controls.AddRange(new Control[] { label, box, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            string trimmed = box.Text.Trim();
            _store.Mutate(s => s.Settings.RelayUrl = trimmed.Length == 0 ? null : trimmed);
        }
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
