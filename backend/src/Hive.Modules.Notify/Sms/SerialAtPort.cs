using System.IO.Ports;
using System.Text;

namespace Hive.Modules.Notify.Sms;

/// <summary>The real serial line of the modem (a USB modem ignores the speed, but the port wants one).</summary>
public sealed class SerialAtPort : IAtPort
{
    private readonly SerialPort _port;

    public SerialAtPort(string name)
    {
        _port = new SerialPort(name, 115200) { ReadTimeout = SerialPort.InfiniteTimeout, WriteTimeout = 3000 };
        _port.Open();
    }

    public async Task<string> ExchangeAsync(string send, Func<string, bool> done, TimeSpan timeout, CancellationToken ct)
    {
        _port.DiscardInBuffer();
        await _port.BaseStream.WriteAsync(Encoding.ASCII.GetBytes(send), ct);
        await _port.BaseStream.FlushAsync(ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var received = new StringBuilder();
        var buffer = new byte[256];
        try
        {
            while (true)
            {
                var n = await _port.BaseStream.ReadAsync(buffer, deadline.Token);
                if (n == 0)
                    continue;
                received.Append(Encoding.ASCII.GetString(buffer, 0, n));
                if (done(received.ToString()))
                    return received.ToString();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer in {timeout.TotalSeconds:0} s (got: {received})");
        }
    }

    public void Dispose() => _port.Dispose();
}
