using QRCoder;
using QuackSnap.Core;
using QuackSnap.Pairing;
using QuackSnap.Protocol;

namespace QuackSnap.UI;

/// <summary>
/// Shows the 6-digit pairing code (primary), plus QR / copyable URI fallbacks,
/// and waits for a device to complete the handshake. The pairing listener only
/// exists while this window is open.
/// </summary>
public sealed class PairingForm : Form
{
    private readonly PairingService _service;
    private readonly StateStore _store;
    private readonly Label _status;

    public PairingForm(Identity identity, StateStore store)
    {
        _store = store;
        _service = new PairingService(identity, store.State.Settings.RelayUrl);

        Text = "Pair a device — QuackSnap";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 640);
        StartPosition = FormStartPosition.CenterScreen;

        string uri = _service.Payload.ToUri();

        var codeCaption = new Label
        {
            Text = "Enter this code in the QuackSnap app:",
            Location = new Point(30, 18),
            Size = new Size(360, 24),
            TextAlign = ContentAlignment.MiddleCenter,
        };

        var code = new Label
        {
            Text = PairingCode.Display(_service.Code),
            Location = new Point(30, 44),
            Size = new Size(360, 64),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 36f, FontStyle.Bold),
        };

        var orScan = new Label
        {
            Text = "…or scan the QR code:",
            Location = new Point(30, 116),
            Size = new Size(360, 22),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
        };

        var qr = new PictureBox
        {
            Location = new Point(75, 142),
            Size = new Size(270, 270),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = RenderQr(uri),
        };

        var codeBox = new TextBox
        {
            Location = new Point(30, 430),
            Width = 300,
            ReadOnly = true,
            Text = uri,
        };
        var copy = new Button { Text = "Copy", Location = new Point(336, 428), Width = 54 };
        copy.Click += (_, _) => { Clipboard.SetText(uri); _status!.Text = "Code copied."; };

        _status = new Label
        {
            Text = "Waiting for a device…",
            Location = new Point(30, 470),
            Size = new Size(360, 60),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font, FontStyle.Bold),
        };

        var hint = new Label
        {
            Text = "The phone finds this PC automatically on your network. If discovery is blocked, scan the QR code or paste the full code above.",
            Location = new Point(30, 540),
            Size = new Size(360, 60),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
        };

        Controls.AddRange(new Control[] { codeCaption, code, orScan, qr, codeBox, copy, _status, hint });

        _service.Paired += OnPaired;
        _service.AttemptsExceeded += () => BeginInvoke(() =>
        {
            _status.Text = "Too many wrong codes — close this window and try again.";
        });
        FormClosed += (_, _) => _service.Dispose();
    }

    private void OnPaired(Device device)
    {
        BeginInvoke(() =>
        {
            bool replaced = _store.State.Devices.Any(d => d.Id == device.Id);
            _store.Mutate(s =>
            {
                s.Devices.RemoveAll(d => d.Id == device.Id);
                s.Devices.Add(device);
            });
            _status.Text = replaced
                ? $"Re-paired with {device.Name} ✓"
                : $"Paired with {device.Name} ✓";
            Logger.Info($"Device saved: {device.Name}");
            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += (_, _) => { timer.Dispose(); Close(); };
            timer.Start();
        });
    }

    private static Image RenderQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 10);
        return Image.FromStream(new MemoryStream(png));
    }
}
