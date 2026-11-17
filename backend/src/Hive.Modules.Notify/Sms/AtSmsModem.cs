using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.Notify.Sms;

/// <summary>One AT command exchange over a serial line: send the bytes, collect what comes back until <c>done</c> says it is enough.</summary>
public interface IAtPort : IDisposable
{
    /// <exception cref="TimeoutException">Nothing satisfying <paramref name="done"/> came within <paramref name="timeout"/>.</exception>
    Task<string> ExchangeAsync(string send, Func<string, bool> done, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Sends SMS over a Quectel EC25 AT port (AT commands manual, SMS chapter: AT+CMGF=0, AT+CMGS) without ModemManager, which would
/// take the port. The port is opened for one message and closed after: the modem is left as it was found, and a modem that
/// was unplugged and plugged back is picked up by the next attempt.
/// </summary>
public sealed partial class AtSmsModem(Func<IAtPort> open, ILogger<AtSmsModem> logger) : ISmsModem
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Network = TimeSpan.FromSeconds(120);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _concatRef = Random.Shared.Next(256);

    [GeneratedRegex(@"\+CE?REG:\s*\d+,\s*(\d+)")]
    private static partial Regex Registration();

    [GeneratedRegex(@"\+CM[ES] ERROR:\s*([^\r\n]+)")]
    private static partial Regex ErrorCode();

    private static bool Finished(string r) => r.Contains("OK\r\n", StringComparison.Ordinal) || r.Contains("ERROR", StringComparison.Ordinal);

    public async Task SendAsync(string number, string text, CancellationToken ct)
    {
        var parts = SmsPdu.Build(number, text, unchecked((byte)Interlocked.Increment(ref _concatRef)));
        await _gate.WaitAsync(ct);
        try
        {
            using var port = Open();
            try
            {
                await InitAsync(port, ct);
                foreach (var part in parts)
                    await SendPartAsync(port, part, ct);
            }
            catch (TimeoutException ex)
            {
                // A modem left at the "> " prompt would eat the next command: leave the prompt.
                await AbortAsync(port);
                throw new SmsModemException("the modem did not answer in time", ex);
            }
            catch (SmsModemException)
            {
                await AbortAsync(port);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private IAtPort Open()
    {
        try
        {
            return open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            throw new SmsModemException($"the modem port cannot be opened: {ex.Message}", ex);
        }
    }

    private static async Task InitAsync(IAtPort port, CancellationToken ct)
    {
        await Expect(port, "ATE0\r", "ATE0", ct);
        await Expect(port, "AT+CMGF=0\r", "AT+CMGF=0", ct);
        var pin = await port.ExchangeAsync("AT+CPIN?\r", Finished, Quick, ct);
        if (!pin.Contains("READY", StringComparison.Ordinal))
            throw new SmsModemException($"the SIM is not ready: {Trim(pin)}");
        if (!await RegisteredAsync(port, ct))
            throw new SmsModemException("the modem is not registered in the network (no signal, or the SIM has no service)");
    }

    /// <summary>On LTE (+CEREG) or on 2G/3G (+CREG): 1 is home, 5 is roaming.</summary>
    private static async Task<bool> RegisteredAsync(IAtPort port, CancellationToken ct)
    {
        foreach (var command in new[] { "AT+CEREG?\r", "AT+CREG?\r" })
        {
            var reply = await port.ExchangeAsync(command, Finished, Quick, ct);
            if (Registration().Match(reply) is { Success: true } m && m.Groups[1].Value is "1" or "5")
                return true;
        }
        return false;
    }

    private async Task SendPartAsync(IAtPort port, SmsPdu.Part part, CancellationToken ct)
    {
        var prompt = await port.ExchangeAsync($"AT+CMGS={part.Length}\r", r => r.Contains('>') || r.Contains("ERROR", StringComparison.Ordinal), Quick, ct);
        if (!prompt.Contains('>'))
            throw new SmsModemException($"the modem refused the message: {Trim(prompt)}");
        var reply = await port.ExchangeAsync(part.Hex + "\u001a", Finished, Network, ct);
        if (!reply.Contains("+CMGS:", StringComparison.Ordinal))
        {
            var why = ErrorCode().Match(reply) is { Success: true } m ? "error " + m.Groups[1].Value.Trim() : Trim(reply);
            throw new SmsModemException($"the network did not take the message: {why}");
        }
        logger.LogDebug("SMS part sent: {Reply}", Trim(reply));
    }

    private static async Task Expect(IAtPort port, string command, string what, CancellationToken ct)
    {
        var reply = await port.ExchangeAsync(command, Finished, Quick, ct);
        if (reply.Contains("ERROR", StringComparison.Ordinal))
            throw new SmsModemException($"{what} failed: {Trim(reply)}");
    }

    private async Task AbortAsync(IAtPort port)
    {
        try
        {
            await port.ExchangeAsync("\u001b", _ => true, TimeSpan.FromSeconds(1), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not leave the SMS prompt");
        }
    }

    private static string Trim(string reply) => reply.Replace("\r", " ").Replace("\n", " ").Trim();
}
