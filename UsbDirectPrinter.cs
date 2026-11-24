using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LabelPrinter;

/// <summary>
/// Writes bytes straight to a USB printer's device, without the Windows print
/// queue. The queue isn't usable for raw label data with the inbox class
/// drivers: with the Microsoft XPS Class Driver its print processor
/// (MS_XPS_PROC) cancels RAW jobs (Win32 error 0x3F) before any byte reaches
/// the printer, leaving the job in "Error". Talking to the device directly
/// works with any driver, and a completed write means the printer took the data.
/// </summary>
internal static class UsbDirectPrinter
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(20);

    public static async Task SendAsync(UsbPrinter printer, byte[] data)
    {
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

        using var handle = CreateFileW(printer.DevicePath, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"No se pudo abrir «{printer.Name}» por USB ({new Win32Exception(error).Message}). ¿Está conectada y encendida?");
        }

        using var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: true);
        using var timeout = new CancellationTokenSource(WriteTimeout);
        try
        {
            await stream.WriteAsync(data, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"«{printer.Name}» no recibió los datos en {WriteTimeout.TotalSeconds:0} segundos. Revisa que tenga papel y la tapa cerrada.");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}
