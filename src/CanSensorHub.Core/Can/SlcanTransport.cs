using System.IO.Ports;
using System.Text;

namespace CanSensorHub.Core.Can;

/// <summary>
/// Real hardware transport for the WeActStudio USB2CANFDV1 adapter running its "slcan" (Lawicel ASCII)
/// firmware, exposed to Windows as a virtual COM port. Implements the same command subset as
/// python-can's slcan backend (which the reference MPCC/MPSWP PC apps use), so it talks to the adapter
/// exactly the same way:
///   - "S&lt;n&gt;\r" selects one of the fixed Lawicel bitrates (0..8), "O\r" opens the channel, "C\r" closes it.
///   - "T&lt;8 hex ID&gt;&lt;1 hex DLC&gt;&lt;data hex&gt;\r" transmits an extended (29-bit) data frame; incoming
///     frames arrive in the same textual form, terminated by CR (0x0D).
/// </summary>
public sealed class SlcanTransport : ICanTransport
{
    // Lawicel standard bitrate codes (S0..S8) understood by slcan firmware.
    private static readonly (int Bps, char Code)[] BitrateTable =
    [
        (10_000, '0'), (20_000, '1'), (50_000, '2'), (100_000, '3'),
        (125_000, '4'), (250_000, '5'), (500_000, '6'), (800_000, '7'), (1_000_000, '8'),
    ];

    public static IReadOnlyList<int> SupportedBitrates { get; } = Array.ConvertAll(BitrateTable, t => t.Bps);

    private readonly SerialPort _port;
    private readonly Thread _readerThread;
    private readonly object _writeLock = new();
    private volatile bool _disposed;

    public string Description { get; }
    public event EventHandler<CanFrameReceivedEventArgs>? FrameReceived;

    private SlcanTransport(SerialPort port, string description)
    {
        _port = port;
        Description = description;
        _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "slcan-rx" };
        _readerThread.Start();
    }

    public static async Task<SlcanTransport> OpenAsync(string portName, int bitrateBps, CancellationToken ct = default)
    {
        var codeEntry = Array.Find(BitrateTable, t => t.Bps == bitrateBps);
        if (codeEntry.Code == default)
            throw new ArgumentException($"Nieobsługiwany bitrate {bitrateBps} bit/s dla adaptera slcan.", nameof(bitrateBps));

        var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 1000,
            NewLine = "\r",
        };

        try
        {
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            // Best-effort reset: close any previously open channel before reconfiguring.
            await WriteCommandAsync(port, "C", ct).ConfigureAwait(false);
            await WriteCommandAsync(port, $"S{codeEntry.Code}", ct).ConfigureAwait(false);
            await WriteCommandAsync(port, "O", ct).ConfigureAwait(false);
        }
        catch
        {
            port.Dispose();
            throw;
        }

        return new SlcanTransport(port, $"slcan {portName} @ {bitrateBps / 1000} kbit/s");
    }

    private static Task WriteCommandAsync(SerialPort port, string command, CancellationToken ct)
        => Task.Run(() => port.Write(command + "\r"), ct);

    public Task SendAsync(CanFrame frame, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sb = new StringBuilder();
        if (frame.IsExtended)
            sb.Append(frame.IsRemote ? 'R' : 'T').Append(frame.Id.ToString("X8"));
        else
            sb.Append(frame.IsRemote ? 'r' : 't').Append(frame.Id.ToString("X3"));
        sb.Append(frame.Data.Length.ToString("X1"));
        if (!frame.IsRemote)
            foreach (var b in frame.Data)
                sb.Append(b.ToString("X2"));
        sb.Append('\r');
        var text = sb.ToString();

        return Task.Run(() =>
        {
            lock (_writeLock)
            {
                if (_disposed) return;
                _port.Write(text);
            }
        }, ct);
    }

    private void ReaderLoop()
    {
        var token = new StringBuilder();
        while (!_disposed)
        {
            int b;
            try
            {
                b = _port.ReadByte();
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception) when (_disposed)
            {
                return;
            }
            catch (Exception)
            {
                if (_disposed) return;
                Thread.Sleep(50);
                continue;
            }

            if (b < 0) continue;

            if (b == '\r')
            {
                if (token.Length > 0)
                {
                    TryParseAndRaise(token.ToString());
                    token.Clear();
                }
                continue;
            }
            if (b == 0x07) // BEL: adapter reported an error for the last command
            {
                token.Clear();
                continue;
            }
            token.Append((char)b);
        }
    }

    private void TryParseAndRaise(string token)
    {
        if (token.Length < 2) return;
        char kind = token[0];
        bool extended = kind is 'T' or 'R';
        bool remote = kind is 'R' or 'r';
        if (kind is not ('T' or 't' or 'R' or 'r')) return;

        int idLen = extended ? 8 : 3;
        if (token.Length < 1 + idLen + 1) return;

        if (!uint.TryParse(token.AsSpan(1, idLen), System.Globalization.NumberStyles.HexNumber, null, out var id))
            return;
        if (!int.TryParse(token.AsSpan(1 + idLen, 1), System.Globalization.NumberStyles.HexNumber, null, out var dlc))
            return;
        dlc = Math.Clamp(dlc, 0, 8);

        byte[] data = Array.Empty<byte>();
        if (!remote)
        {
            int dataStart = 1 + idLen + 1;
            int available = (token.Length - dataStart) / 2;
            int n = Math.Min(dlc, Math.Max(available, 0));
            data = new byte[n];
            for (int i = 0; i < n; i++)
            {
                if (!byte.TryParse(token.AsSpan(dataStart + i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out data[i]))
                    return;
            }
        }

        try
        {
            var frame = new CanFrame(id, data, extended, remote);
            FrameReceived?.Invoke(this, new CanFrameReceivedEventArgs(frame));
        }
        catch (ArgumentOutOfRangeException)
        {
            // malformed line from the adapter — ignore
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            lock (_writeLock)
            {
                if (_port.IsOpen)
                {
                    _port.Write("C\r");
                }
            }
        }
        catch
        {
            // best-effort close command
        }
        try { _readerThread.Join(TimeSpan.FromMilliseconds(600)); } catch { /* ignore */ }
        try { _port.Dispose(); } catch { /* ignore */ }
    }
}
