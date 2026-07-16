using QuackSnap.Core;
using QuackSnap.Transfer;
using QuackSnap.UI;
using Velopack;

namespace QuackSnap;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Must run first: handles Velopack install/update/uninstall hooks and
        // then returns to normal startup. Also wires up self-updating.
        VelopackApp.Build().Run();

        using var singleInstance = new Mutex(initiallyOwned: true, "QuackSnap-single-instance", out bool isFirst);
        if (!isFirst) return;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuackSnap");
        Directory.CreateDirectory(appDataDir);
        Logger.Init(appDataDir);

        ApplicationConfiguration.Initialize();

        try
        {
            var identity = Identity.LoadOrCreate(appDataDir);
            var store = new StateStore(appDataDir);
            var queue = new TransferQueue(appDataDir);
            var transport = new TlsTransport(identity);
            var relay = new RelaySender(identity, () => store.State.Settings.RelayUrl);
            using var coordinator = new SendCoordinator(queue, store, relay, transport);
            using var receiveServer = new ReceiveServer(identity, store);
            receiveServer.Start();

            Application.Run(new TrayApplicationContext(identity, store, queue, coordinator, receiveServer));
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal", ex);
            MessageBox.Show($"QuackSnap failed to start:\n{ex.Message}", "QuackSnap",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
