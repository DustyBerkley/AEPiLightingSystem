using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AEPiLightingSystem.Dmx;

/// <summary>
/// Continuously transmits a <see cref="DmxUniverse"/> out an Enttec Open DMX
/// USB (70303) interface. That widget has no onboard microcontroller or
/// frame buffer - per Enttec's own datasheet, "output is processed directly
/// on the computer's processor" - so generating DMX512's break/mark-after-
/// break framing is entirely this class's job, every frame, for as long as
/// the port is open.
///
/// Talks to the FTDI chip through FTDI's D2XX driver (ftd2xx.dll) rather
/// than the Windows virtual-COM-port/SerialPort path. An earlier version of
/// this class went through SerialPort and faked a break by switching to a
/// slow baud rate and writing a zero byte - that relies on precise UART bit
/// timing surviving a trip through the VCP driver's buffering, which by
/// default batches writes for up to 16ms (its "latency timer") - easily
/// enough to corrupt or drop the break entirely, which is exactly what was
/// observed against real fixtures (silently no output, while 3rd-party
/// software using the same dongle worked fine). D2XX exposes a real
/// hardware break control (<see cref="FT_SetBreakOn"/>/
/// <see cref="FT_SetBreakOff"/>) that directly toggles the line state
/// instead of encoding it as bits at a chosen baud rate, and lets the
/// latency timer be set low in software (no registry/admin access needed)
/// - both close the gap with how dedicated lighting software drives this
/// same class of adapter.
/// </summary>
public sealed class DmxOutputService : IDisposable
{
    private const uint DmxBaudRate = 250000;
    private const byte DataBits8 = 8;
    private const byte StopBits2 = 2;
    private const byte ParityNone = 0;
    private const ushort FlowControlNone = 0;
    private const uint PurgeRxAndTx = 3; // FT_PURGE_RX (1) | FT_PURGE_TX (2)
    private const byte LatencyTimerMs = 1;
    private const uint OpenBySerialNumber = 1;

    // A break only needs to hold the line low for >=88us and the
    // mark-after-break only needs to be >=12us - a millisecond of slack on
    // each comfortably covers both regardless of OS scheduling jitter, since
    // (unlike the old baud-rate trick) the line state itself is held by the
    // FTDI chip rather than encoded by exact timing on this thread.
    private const int BreakHoldMs = 1;
    private const int MarkAfterBreakMs = 1;
    private const int FrameSettleMarginMs = 2;

    // A single failed frame (one dropped USB transfer, one GC pause landing
    // at the wrong moment) used to kill the sender thread outright - DMX
    // output would go dark and stay dark until manually reconnecting. Retry
    // through isolated failures instead, and only give up - and tell the UI
    // - once they're persistent enough to mean the device is actually gone.
    private const int MaxConsecutiveFailures = 30;

    // How often the sender loop re-enumerates FTDI devices to confirm the
    // one it opened is still physically plugged in - see SenderLoop's own
    // comment on why write failures alone don't reliably catch this.
    private const int DevicePresenceCheckIntervalMs = 1000;

    private readonly DmxUniverse _universe;
    private IntPtr _handle = IntPtr.Zero;
    private string? _connectedSerialNumber;
    private Thread? _senderThread;
    private volatile bool _running;

    public event EventHandler<Exception>? ConnectionLost;

    public bool IsConnected => _handle != IntPtr.Zero;

    public DmxOutputService(DmxUniverse universe)
    {
        _universe = universe;
    }

    /// <summary>The FTDI serial number of every Open DMX USB-class device currently plugged in.</summary>
    public static string[] GetAvailablePorts()
    {
        var status = FT_CreateDeviceInfoList(out var count);
        if (status != FtOk || count == 0)
            return Array.Empty<string>();

        var nodes = new FT_DEVICE_LIST_INFO_NODE[count];
        status = FT_GetDeviceInfoList(nodes, ref count);
        if (status != FtOk)
            return Array.Empty<string>();

        var serials = new string[count];
        for (var i = 0; i < count; i++)
            serials[i] = ReadFixedAnsiString(nodes[i].SerialNumber);

        return serials;
    }

    public void Connect(string serialNumber)
    {
        Disconnect();

        var status = FT_OpenEx(serialNumber, OpenBySerialNumber, out var handle);
        if (status != FtOk)
            throw new InvalidOperationException($"FT_OpenEx failed ({status}) for device '{serialNumber}'.");

        try
        {
            ThrowIfFailed(FT_SetLatencyTimer(handle, LatencyTimerMs), nameof(FT_SetLatencyTimer));
            ThrowIfFailed(FT_SetBaudRate(handle, DmxBaudRate), nameof(FT_SetBaudRate));
            ThrowIfFailed(FT_SetDataCharacteristics(handle, DataBits8, StopBits2, ParityNone), nameof(FT_SetDataCharacteristics));
            ThrowIfFailed(FT_SetFlowControl(handle, FlowControlNone, 0, 0), nameof(FT_SetFlowControl));
            ThrowIfFailed(FT_Purge(handle, PurgeRxAndTx), nameof(FT_Purge));
        }
        catch
        {
            FT_Close(handle);
            throw;
        }

        _handle = handle;
        _connectedSerialNumber = serialNumber;
        _running = true;

        _senderThread = new Thread(SenderLoop) { IsBackground = true, Name = "DMX Sender", Priority = ThreadPriority.Highest };
        _senderThread.Start();
    }

    public void Disconnect()
    {
        _running = false;
        _senderThread?.Join(200);
        _senderThread = null;

        if (_handle != IntPtr.Zero)
        {
            FT_Close(_handle);
            _handle = IntPtr.Zero;
        }

        _connectedSerialNumber = null;
    }

    private void SenderLoop()
    {
        var handle = _handle;
        var serialNumber = _connectedSerialNumber;
        if (handle == IntPtr.Zero || serialNumber is null)
            return;

        var consecutiveFailures = 0;
        var lastDevicePresenceCheck = DateTime.UtcNow;

        while (_running)
        {
            // A physically unplugged FTDI device doesn't reliably surface as
            // a write/IO error on the open handle - the D2XX driver can keep
            // returning success on a stale handle for a while, which left
            // the write-failure counter below never actually tripping on a
            // real unplug. Re-enumerating and checking this device is still
            // in the list is what actually catches that.
            var now = DateTime.UtcNow;
            if ((now - lastDevicePresenceCheck).TotalMilliseconds >= DevicePresenceCheckIntervalMs)
            {
                lastDevicePresenceCheck = now;
                if (Array.IndexOf(GetAvailablePorts(), serialNumber) < 0)
                {
                    _running = false;
                    ConnectionLost?.Invoke(this, new InvalidOperationException($"Device '{serialNumber}' is no longer present."));
                    return;
                }
            }

            try
            {
                ThrowIfFailed(FT_SetBreakOn(handle), nameof(FT_SetBreakOn));
                Thread.Sleep(BreakHoldMs);
                ThrowIfFailed(FT_SetBreakOff(handle), nameof(FT_SetBreakOff));
                Thread.Sleep(MarkAfterBreakMs);

                var frame = _universe.Frame;
                var length = (uint)_universe.FrameLength;
                ThrowIfFailed(FT_Write(handle, frame, length, out var written), nameof(FT_Write));
                if (written != length)
                    throw new InvalidOperationException($"FT_Write only sent {written}/{length} bytes.");

                // FT_Write returns once the driver has accepted the bytes,
                // not once the FTDI chip has actually finished shifting them
                // out the wire at 250000 baud - asserting the next break
                // before that finishes truncates this frame mid-transmission,
                // which a receiver reads as a burst of garbage/wrong-color
                // frames rather than a clean hold. 11 bits/byte (1 start + 8
                // data + 2 stop) at 250000 baud, plus a small margin, is
                // enough to let real transmission catch up before looping.
                var frameTimeMs = length * 11.0 / DmxBaudRate * 1000.0;
                Thread.Sleep((int)Math.Ceiling(frameTimeMs) + FrameSettleMarginMs);

                consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                System.Diagnostics.Debug.WriteLine($"DMX frame send failed ({consecutiveFailures}/{MaxConsecutiveFailures}): {ex.Message}");

                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _running = false;
                    ConnectionLost?.Invoke(this, ex);
                    return;
                }
            }
        }
    }

    private static void ThrowIfFailed(int status, string call)
    {
        if (status != FtOk)
            throw new InvalidOperationException($"{call} failed ({status}).");
    }

    private static string ReadFixedAnsiString(byte[] bytes)
    {
        var nullIndex = Array.IndexOf(bytes, (byte)0);
        var length = nullIndex >= 0 ? nullIndex : bytes.Length;
        return Encoding.ASCII.GetString(bytes, 0, length);
    }

    public void Dispose() => Disconnect();

    // --- FTDI D2XX interop (ftd2xx.dll) ---
    // Signatures/constants per FTDI's "D2XX Programmer's Guide" (FT_STATUS
    // return codes: 0 = FT_OK).

    private const int FtOk = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct FT_DEVICE_LIST_INFO_NODE
    {
        public uint Flags;
        public uint Type;
        public uint Id;
        public uint LocId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] SerialNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Description;
        public IntPtr FtHandle;
    }

    [DllImport("ftd2xx.dll")]
    private static extern int FT_CreateDeviceInfoList(out uint numDevs);

    [DllImport("ftd2xx.dll")]
    private static extern int FT_GetDeviceInfoList([In, Out] FT_DEVICE_LIST_INFO_NODE[] deviceInfo, ref uint numDevs);

    [DllImport("ftd2xx.dll", CharSet = CharSet.Ansi)]
    private static extern int FT_OpenEx(string serialNumber, uint flags, out IntPtr ftHandle);

    [DllImport("ftd2xx.dll")]
    private static extern int FT_Close(IntPtr ftHandle);

    [DllImport("ftd2xx.dll")]
    private static extern int FT_SetBaudRate(IntPtr ftHandle, uint baudRate);

    [DllImport("ftd2xx.dll")]
    private static extern int FT_SetDataCharacteristics(IntPtr ftHandle, byte wordLength, byte stopBits, byte parity);

    [DllImport("ftd2xx.dll")]
    private static extern int FT_SetFlowControl(IntPtr ftHandle, ushort flowControl, byte xOnChar, byte xOffChar);

    [DllImport("ftd2xx.dll")]
    private static extern int FT_SetLatencyTimer(IntPtr ftHandle, byte latencyMs);

    [DllImport("ftd2xx.dll")]
    private static extern int FT_Purge(IntPtr ftHandle, uint mask);

    [DllImport("ftd2xx.dll")]
    private static extern int FT_SetBreakOn(IntPtr ftHandle);

    [DllImport("ftd2xx.dll")]
    private static extern int FT_SetBreakOff(IntPtr ftHandle);

    [DllImport("ftd2xx.dll")]
    private static extern int FT_Write(IntPtr ftHandle, byte[] buffer, uint bytesToWrite, out uint bytesWritten);
}
