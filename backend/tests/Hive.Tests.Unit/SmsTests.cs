using Hive.Modules.Notify.Sms;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hive.Tests.Unit;

public class SmsPduTests
{
    [Fact]
    public void A_short_russian_text_is_one_ucs2_message()
    {
        var part = Assert.Single(SmsPdu.Build("+79161234567", "Привет", 7));
        // SMSC 00, SMS-SUBMIT 01, MR 00, 11 digits, international, swapped BCD, PID 00, DCS UCS2 08, 12 bytes of text.
        Assert.Equal("0001000B919761214365F700080C041F04400438043204350442", part.Hex);
        Assert.Equal(25, part.Length); // without the SMSC byte
    }

    [Fact]
    public void A_long_text_is_cut_into_parts_of_67_with_a_concatenation_header()
    {
        var parts = SmsPdu.Build("+79161234567", new string('а', 100), 0xAB);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.StartsWith("004100", p.Hex)); // UDHI set
        Assert.Contains("050003AB0201", parts[0].Hex);
        Assert.Contains("050003AB0202", parts[1].Hex);
        Assert.Equal("8C", parts[0].Hex.Substring(26, 2)); // UDL of the first part: 6 + 134 = 140
    }

    [Fact]
    public void A_surrogate_pair_is_never_split_between_parts()
    {
        var text = new string('a', 66) + "😀" + new string('b', 10);
        var chunks = SmsPdu.Split(text);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(66, chunks[0].Length);
        Assert.StartsWith("😀", chunks[1]);
    }

    [Fact]
    public void A_text_longer_than_three_parts_is_cut_with_an_ellipsis()
    {
        var chunks = SmsPdu.Split(new string('x', 500));
        Assert.Equal(SmsPdu.MaxParts, chunks.Count);
        Assert.Equal(SmsPdu.MaxParts * SmsPdu.PartMax, chunks.Sum(c => c.Length));
        Assert.EndsWith("…", chunks[^1]);
    }

    [Fact]
    public void A_text_of_exactly_70_characters_stays_one_message() =>
        Assert.Single(SmsPdu.Split(new string('я', SmsPdu.SingleMax)));
}

public class SmsNumberTests
{
    [Theory]
    [InlineData("+79161234567", "+79161234567")]
    [InlineData("89161234567", "+79161234567")]
    [InlineData("8 (916) 123-45-67", "+79161234567")]
    [InlineData("79161234567", "+79161234567")]
    [InlineData("+375 29 123 45 67", "+375291234567")]
    [InlineData("12345", null)]
    [InlineData("phone", null)]
    [InlineData("", null)]
    public void Numbers_are_normalized_to_plus_digits(string input, string? expected) =>
        Assert.Equal(expected, SmsNumber.Normalize(input));

    [Fact]
    public void A_list_is_split_deduplicated_and_cleaned_of_garbage()
    {
        var numbers = SmsNumber.ParseList("89161234567; +79161234567, oops,\n +79037654321");
        Assert.Equal(["+79161234567", "+79037654321"], numbers);
    }

    [Fact]
    public void Sms_is_enabled_only_with_a_port_and_a_valid_number()
    {
        Assert.False(new SmsOptions { Port = "/dev/ttyUSB2" }.Enabled);
        Assert.False(new SmsOptions { Recipients = "89161234567" }.Enabled);
        Assert.False(new SmsOptions { Port = "/dev/ttyUSB2", Recipients = "oops" }.Enabled);
        Assert.True(new SmsOptions { Port = "/dev/ttyUSB2", Recipients = "89161234567" }.Enabled);
    }
}

public class AtSmsModemTests
{
    /// <summary>A modem on a table: every command is answered by <c>reply</c>; null reply means silence (a timeout).</summary>
    private sealed class FakePort(Func<string, string?> reply) : IAtPort
    {
        public List<string> Sent { get; } = [];
        public bool Disposed { get; private set; }

        public Task<string> ExchangeAsync(string send, Func<string, bool> done, TimeSpan timeout, CancellationToken ct)
        {
            Sent.Add(send);
            var answer = reply(send) ?? throw new TimeoutException("silence");
            return Task.FromResult(answer);
        }

        public void Dispose() => Disposed = true;
    }

    private static string? Healthy(string command) => command switch
    {
        "ATE0\r" or "AT+CMGF=0\r" => "\r\nOK\r\n",
        "AT+CPIN?\r" => "\r\n+CPIN: READY\r\n\r\nOK\r\n",
        "AT+CEREG?\r" => "\r\n+CEREG: 0,1\r\n\r\nOK\r\n",
        _ when command.StartsWith("AT+CMGS=", StringComparison.Ordinal) => "\r\n> ",
        _ when command.EndsWith('\u001a') => "\r\n+CMGS: 12\r\n\r\nOK\r\n",
        _ => "\r\nOK\r\n",
    };

    private static AtSmsModem Modem(FakePort port) => new(() => port, NullLogger<AtSmsModem>.Instance);

    [Fact]
    public async Task A_message_goes_through_the_init_and_the_cmgs_dialogue()
    {
        var port = new FakePort(Healthy);
        await Modem(port).SendAsync("+79161234567", "Привет", CancellationToken.None);

        Assert.Equal(["ATE0\r", "AT+CMGF=0\r", "AT+CPIN?\r", "AT+CEREG?\r", "AT+CMGS=25\r"], port.Sent.Take(5));
        Assert.Equal("0001000B919761214365F700080C041F04400438043204350442\u001a", port.Sent[5]);
        Assert.True(port.Disposed);
    }

    [Fact]
    public async Task A_roaming_registration_on_2g_is_enough()
    {
        var port = new FakePort(c => c switch
        {
            "AT+CEREG?\r" => "\r\n+CEREG: 0,0\r\n\r\nOK\r\n",
            "AT+CREG?\r" => "\r\n+CREG: 0,5\r\n\r\nOK\r\n",
            _ => Healthy(c),
        });
        await Modem(port).SendAsync("+79161234567", "тест", CancellationToken.None);
        Assert.Contains("AT+CREG?\r", port.Sent);
    }

    [Fact]
    public async Task No_network_is_reported_and_nothing_is_sent()
    {
        var port = new FakePort(c => c.StartsWith("AT+CE", StringComparison.Ordinal) || c.StartsWith("AT+CR", StringComparison.Ordinal)
            ? "\r\n+CEREG: 0,2\r\n\r\nOK\r\n"
            : Healthy(c));
        var ex = await Assert.ThrowsAsync<SmsModemException>(() => Modem(port).SendAsync("+79161234567", "тест", CancellationToken.None));
        Assert.Contains("not registered", ex.Message);
        Assert.DoesNotContain(port.Sent, s => s.StartsWith("AT+CMGS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sim_that_wants_a_pin_is_reported()
    {
        var port = new FakePort(c => c == "AT+CPIN?\r" ? "\r\n+CPIN: SIM PIN\r\n\r\nOK\r\n" : Healthy(c));
        var ex = await Assert.ThrowsAsync<SmsModemException>(() => Modem(port).SendAsync("+79161234567", "тест", CancellationToken.None));
        Assert.Contains("SIM is not ready", ex.Message);
    }

    [Fact]
    public async Task A_network_error_carries_its_code()
    {
        var port = new FakePort(c => c.EndsWith('\u001a') ? "\r\n+CMS ERROR: 331\r\n" : Healthy(c));
        var ex = await Assert.ThrowsAsync<SmsModemException>(() => Modem(port).SendAsync("+79161234567", "тест", CancellationToken.None));
        Assert.Contains("331", ex.Message);
        Assert.Contains("\u001b", port.Sent); // ESC: the modem is left out of the prompt
    }

    [Fact]
    public async Task A_silent_modem_is_a_modem_error_not_a_hang_or_a_crash()
    {
        var port = new FakePort(c => c == "AT+CPIN?\r" ? null : Healthy(c));
        var ex = await Assert.ThrowsAsync<SmsModemException>(() => Modem(port).SendAsync("+79161234567", "тест", CancellationToken.None));
        Assert.Contains("did not answer", ex.Message);
        Assert.True(port.Disposed);
    }

    [Fact]
    public async Task A_port_that_cannot_be_opened_is_a_modem_error()
    {
        var modem = new AtSmsModem(() => throw new FileNotFoundException("/dev/ttyUSB2"), NullLogger<AtSmsModem>.Instance);
        var ex = await Assert.ThrowsAsync<SmsModemException>(() => modem.SendAsync("+79161234567", "тест", CancellationToken.None));
        Assert.Contains("cannot be opened", ex.Message);
    }
}
