namespace Hive.Modules.Notify.Sms;

public interface ISmsModem
{
    /// <summary>Sends one message; returns when the network has accepted it. Throws <see cref="SmsModemException"/> when it could not.</summary>
    Task SendAsync(string number, string text, CancellationToken ct);
}

public sealed class SmsModemException(string message, Exception? inner = null) : Exception(message, inner);
