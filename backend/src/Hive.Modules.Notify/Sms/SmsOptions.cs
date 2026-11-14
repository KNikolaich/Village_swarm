namespace Hive.Modules.Notify.Sms;

/// <summary>
/// SMS for critical events through a USB LTE modem on this machine (docs/sms-keenetic.md). Off until a serial port
/// and at least one recipient are set; the Keenetic router does not send SMS for Huawei E3372, so the modem
/// (Quectel EC25/EG25 and alike) sits in the hive's own mini PC and is driven with AT commands.
/// </summary>
public sealed class SmsOptions
{
    public const string Section = "Sms";

    /// <summary>The modem's AT port, for example <c>/dev/ttyUSB3</c> (Quectel: ttyUSB2 or ttyUSB3). Empty disables SMS.</summary>
    public string? Port { get; set; }

    public int BaudRate { get; set; } = 115200;

    /// <summary>Phone numbers in the international form, <c>+79991234567</c>.</summary>
    public List<string> Recipients { get; set; } = [];

    /// <summary>A cap on SMS per hour so that a looping alarm does not empty the SIM balance (spec 9.6).</summary>
    public int MaxPerHour { get; set; } = 6;

    /// <summary>The same alarm from the same device again within this window is not sent again.</summary>
    public int DedupeMin { get; set; } = 30;

    /// <summary>SIM PIN, when the SIM asks for one. Only in .env.</summary>
    public string? Pin { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Port) && Recipients.Any(r => !string.IsNullOrWhiteSpace(r));
}
