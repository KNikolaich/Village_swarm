using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Users;

public sealed class SmtpOptions
{
    public const string Section = "Smtp";

    /// <summary>For example smtp.yandex.ru. Port 587 with STARTTLS: implicit TLS on 465 is not supported.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? User { get; set; }

    /// <summary>An app password of the mailbox, not its main password.</summary>
    public string? Password { get; set; }

    /// <summary>Sender address; most servers want the same mailbox as <see cref="User"/>.</summary>
    public string? From { get; set; }

    public string FromName { get; set; } = "Улей";

    /// <summary>Letters are written as .eml files here instead of being sent (tests, a hive without mail).</summary>
    public string? PickupDirectory { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(From) && (!string.IsNullOrWhiteSpace(Host) || !string.IsNullOrWhiteSpace(PickupDirectory));
}

/// <summary>Letters for password recovery. No mail settings: no recovery by email.</summary>
public interface IMailSender
{
    bool Configured { get; }

    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

public sealed class SmtpMailSender(IOptions<SmtpOptions> options) : IMailSender
{
    private readonly SmtpOptions _options = options.Value;

    public bool Configured => _options.Configured;

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        if (!Configured)
            throw new InvalidOperationException("Mail is not configured (Smtp:Host, Smtp:From)");
        using var message = new MailMessage(new MailAddress(_options.From!, _options.FromName), new MailAddress(to))
        {
            Subject = subject,
            Body = body,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
        };
        using var client = new SmtpClient();
        if (!string.IsNullOrWhiteSpace(_options.PickupDirectory))
        {
            Directory.CreateDirectory(_options.PickupDirectory);
            client.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
            client.PickupDirectoryLocation = _options.PickupDirectory;
            message.BodyTransferEncoding = System.Net.Mime.TransferEncoding.EightBit; // readable .eml files
        }
        else
        {
            client.Host = _options.Host!;
            client.Port = _options.Port;
            client.EnableSsl = _options.UseSsl;
            client.Timeout = 20_000;
            if (!string.IsNullOrEmpty(_options.User))
                client.Credentials = new NetworkCredential(_options.User, _options.Password);
        }
        await client.SendMailAsync(message, ct);
    }
}
