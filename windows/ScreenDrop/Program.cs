using ScreenDrop.Core;
using ScreenDrop.Transfer;
using ScreenDrop.UI;

namespace ScreenDrop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(initiallyOwned: true, "ScreenDrop-single-instance", out bool isFirst);
        if (!isFirst) return;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenDrop");
        Directory.CreateDirectory(appDataDir);
        Logger.Init(appDataDir);

        ApplicationConfiguration.Initialize();

        try
        {
            var identity = Identity.LoadOrCreate(appDataDir);
            var store = new StateStore(appDataDir);
            var queue = new TransferQueue(appDataDir);
            var transport = new TlsTransport(identity);
            using var coordinator = new SendCoordinator(queue, store, transport);

            Application.Run(new TrayApplicationContext(identity, store, queue, coordinator));
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal", ex);
            MessageBox.Show($"ScreenDrop failed to start:\n{ex.Message}", "ScreenDrop",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
