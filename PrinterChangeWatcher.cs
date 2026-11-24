using System.Runtime.InteropServices;

namespace LabelPrinter;

/// <summary>
/// Raises <see cref="Changed"/> (on a background thread) when a printer is
/// added, removed or changed in the local spooler. Device plug/unplug is
/// caught separately by the window (WM_DEVICECHANGE); this covers the queue
/// that Windows creates a moment after a new USB printer is connected.
/// </summary>
internal sealed class PrinterChangeWatcher : IDisposable
{
    private const uint PRINTER_CHANGE_PRINTER = 0x000000FF;
    private const uint WAIT_OBJECT_0 = 0;

    public event Action? Changed;

    private readonly ManualResetEvent _stop = new(false);
    private Thread? _thread;

    public void Start()
    {
        if (_thread is not null)
            return;
        _thread = new Thread(Run) { IsBackground = true, Name = "PrinterChangeWatcher" };
        _thread.Start();
    }

    private void Run()
    {
        // Retry if the spooler is restarted (the handles go stale).
        while (!_stop.WaitOne(0))
        {
            if (!OpenPrinterW(null, out IntPtr server, IntPtr.Zero))
            {
                if (_stop.WaitOne(5000)) return;
                continue;
            }
            IntPtr change = FindFirstPrinterChangeNotification(server, PRINTER_CHANGE_PRINTER, 0, IntPtr.Zero);
            if (change == new IntPtr(-1))
            {
                ClosePrinter(server);
                if (_stop.WaitOne(5000)) return;
                continue;
            }
            try
            {
                var handles = new[] { change, _stop.SafeWaitHandle.DangerousGetHandle() };
                while (true)
                {
                    uint r = WaitForMultipleObjects(2, handles, false, 0xFFFFFFFF);
                    if (r != WAIT_OBJECT_0)
                        return; // stop signalled (or wait failed)
                    if (!FindNextPrinterChangeNotification(change, out _, IntPtr.Zero, IntPtr.Zero))
                        break;
                    Changed?.Invoke();
                }
            }
            finally
            {
                FindClosePrinterChangeNotification(change);
                ClosePrinter(server);
            }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(2000);
        _stop.Dispose();
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string? name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern IntPtr FindFirstPrinterChangeNotification(IntPtr printer, uint filter, uint options, IntPtr notifyOptions);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool FindNextPrinterChangeNotification(IntPtr change, out uint what, IntPtr options, IntPtr info);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool FindClosePrinterChangeNotification(IntPtr change);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);
}
