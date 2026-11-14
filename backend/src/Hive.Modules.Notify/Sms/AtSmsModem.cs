using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Notify.Sms;

/// <summary>Sends one SMS to one number; throws when the modem did not accept it.</summary>
public interface ISmsModem
{
    Task SendAsync(string number, string text, CancellationToken ct);
}

/// <summary>A line-oriented AT command channel: the serial port in production, a fake in tests.</summary>
public interface IAtChannel : IDisposable
{
    Task WriteAsync(string data, CancellationToken ct);

    /// <summary>Reads until one of <paramref name="terminators"/> is in the reply or the timeout passes; returns what came.</summary>
    Task<string> ReadUntilAsync(string[] terminators, TimeSpan timeout, CancellationToken ct);
}

public sealed class SmsException(string message) : Exception(message);

/// <summary>
/// SMS through AT commands (<c>AT+CMGF=0</c>, <c>AT+CMGS</c>), the way Quectel EC25/EG25 and SIMCom modems take them
/// (Quectel AT Commands Manual, 3GPP TS 27.005). One send at a time: a modem has one AT port.
/// </summary>
public sealed class AtSmsModem(Func<IAtChannel> openChannel, IOptions<SmsOptions> options, ILogger<AtSmsModem> logger) : ISmsModem
{
    private static readonly string[] Final = ["OK\r", "ERROR", "+CMS ERROR", "+CME ERROR"];
    private static readonly string[] Prompt = ["> ", ">"];
    private readonly SemaphoreSlim _one = new(1, 1);
    private int _reference;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AtSmsModem(IOptions<SmsOptions> options, ILogger<AtSmsModem> logger)
        : this(() => new SerialAtChannel(options.Value.Port!, options.Value.BaudRate), options, logger)
    {
    }

    public async Task SendAsync(string number, string text, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            using var port = openChannel();
            await InitAsync(port, ct);
            var reference = (byte)(Interlocked.Increment(ref _reference) & 0xFF);
            foreach (var part in SmsPdu.Encode(number, text, reference))
                await SendPartAsync(port, part, ct);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task InitAsync(IAtChannel port, CancellationToken ct)
    {
        await CommandAsync(port, "ATE0", ct); // echo off, so that replies hold only replies
        var sim = await CommandAsync(port, "AT+CPIN?", ct);
        if (sim.Contains("SIM PIN", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(options.Value.Pin))
                throw new SmsException("The SIM asks for a PIN (Sms:Pin)");
            await CommandAsync(port, $"AT+CPIN=\"{options.Value.Pin}\"", ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        else if (!sim.Contains("READY", StringComparison.Ordinal))
        {
            throw new SmsException($"SIM is not ready: {Trim(sim)}");
        }
        await CommandAsync(port, "AT+CMGF=0", ct); // PDU mode
    }

    private async Task SendPartAsync(IAtChannel port, SmsPart part, CancellationToken ct)
    {
        await port.WriteAsync($"AT+CMGS={part.TpduLength}\r", ct);
        var prompt = await port.ReadUntilAsync(Prompt.Concat(Final).ToArray(), TimeSpan.FromSeconds(5), ct);
        if (!Prompt.Any(p => prompt.Contains(p, StringComparison.Ordinal)))
            throw new SmsException($"The modem did not ask for the message: {Trim(prompt)}");

        await port.WriteAsync(part.Hex + "\x1A", ct); // Ctrl-Z ends the message
        // The network can take a while to confirm: up to 60 s per part.
        var reply = await port.ReadUntilAsync(Final, TimeSpan.FromSeconds(60), ct);
        if (!reply.Contains("+CMGS:", StringComparison.Ordinal) || reply.Contains("ERROR", StringComparison.Ordinal))
            throw new SmsException($"The modem did not send the message: {Trim(reply)}");
        logger.LogDebug("SMS part sent: {Reply}", Trim(reply));
    }

    private static async Task<string> CommandAsync(IAtChannel port, string command, CancellationToken ct)
    {
        await port.WriteAsync(command + "\r", ct);
        var reply = await port.ReadUntilAsync(Final, TimeSpan.FromSeconds(10), ct);
        if (reply.Contains("ERROR", StringComparison.Ordinal))
            throw new SmsException($"{command}: {Trim(reply)}");
        if (!reply.Contains("OK", StringComparison.Ordinal))
            throw new SmsException($"{command}: no answer from the modem");
        return reply;
    }

    private static string Trim(string s) => s.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}

/// <summary>The modem's AT port as a serial port (System.IO.Ports; the device is passed into the container).</summary>
public sealed class SerialAtChannel : IAtChannel
{
    private readonly SerialPort _port;

    public SerialAtChannel(string name, int baud)
    {
        _port = new SerialPort(name, baud) { NewLine = "\r", ReadTimeout = 500, WriteTimeout = 5000, Encoding = Encoding.ASCII };
        _port.Open();
        _port.DiscardInBuffer();
    }

    public Task WriteAsync(string data, CancellationToken ct)
    {
        _port.Write(data);
        return Task.CompletedTask;
    }

    public async Task<string> ReadUntilAsync(string[] terminators, TimeSpan timeout, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (_port.BytesToRead == 0)
            {
                await Task.Delay(50, ct);
                continue;
            }
            sb.Append(_port.ReadExisting());
            var s = sb.ToString();
            if (terminators.Any(t => s.Contains(t, StringComparison.Ordinal)))
                return s;
        }
        return sb.ToString();
    }

    public void Dispose() => _port.Dispose();
}
