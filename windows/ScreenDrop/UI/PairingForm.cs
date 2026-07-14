using QRCoder;
using ScreenDrop.Core;
using ScreenDrop.Pairing;

namespace ScreenDrop.UI;

/// <summary>
/// Shows the QR code / pairing string and waits for a device to complete the
/// handshake. The pairing listener only exists while this window is open.
/// </summary>
public sealed class PairingForm : Form
{
    private readonly PairingService _service;
    private readonly StateStore _store;
    private readonly Label _status;

    public PairingForm(Identity identity, StateStore store)
    {
        _store = store;
        _service = new PairingService(identity);

        Text = "Pair a device — ScreenDrop";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 560);
        StartPosition = FormStartPosition.CenterScreen;

        string uri = _service.Payload.ToUri();

        var qr = new PictureBox
        {
            Location = new Point(30, 20),
            Size = new Size(360, 360),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = RenderQr(uri),
        };

        var instructions = new Label
        {
            Text = "Scan with the ScreenDrop app, or paste the code below into a receiver.",
            Location = new Point(30, 388),
            Size = new Size(360, 36),
            TextAlign = ContentAlignment.MiddleCenter,
        };

        var code = new TextBox
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

        Controls.AddRange(new Control[] { qr, instructions, code, copy, _status });

        _service.Paired += OnPaired;
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
