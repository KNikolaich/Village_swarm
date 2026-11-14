using Hive.Modules.Notify.Sms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hive.Tests.Unit.Sms;

public sealed class SmsTests
{
    [Fact]
    public void Pdu_of_a_short_russian_text_is_one_ucs2_part()
    {
        var part = Assert.Single(SmsPdu.Encode("+79161234567", "Привет"));
        Assert.Equal("00" + "0100" + "0B91" + "9761214365F7" + "0008" + "0C" + "041F04400438043204350442", part.Hex);
        Assert.Equal(25, part.TpduLength);
    }

    [Fact]
    public void Pdu_of_a_national_number_uses_the_national_type()
    {
        var part = Assert.Single(SmsPdu.Encode("89161234567", "a"));
        Assert.StartsWith("00" + "0100" + "0B81", part.Hex);
    }

    [Fact]
    public void A_long_text_becomes_a_concatenated_message_that_never_cuts_a_pair()
    {
        var text = new string('я', 66) + "😀" + new string('я', 40); // the emoji straddles the 67th character
        var parts = SmsPdu.Encode("+79161234567", text, reference: 7);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.StartsWith("0041", p.Hex[..4]));
        Assert.Contains("050003" + "07" + "02" + "01", parts[0].Hex);
        Assert.Contains("050003" + "07" + "02" + "02", parts[1].Hex);
        var chunks = SmsPdu.Split(text);
        Assert.Equal(66, chunks[0].Length);
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void Throttle_drops_the_same_alarm_and_caps_the_hour()
    {
        var t = new SmsThrottle(maxPerHour: 2, dedupe: TimeSpan.FromMinutes(30));
        var now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        Assert.True(t.TryTake("a:leak", now));
        Assert.False(t.TryTake("a:leak", now.AddMinutes(5)));
        Assert.True(t.TryTake("b:leak", now.AddMinutes(6)));
        Assert.False(t.TryTake("c:leak", now.AddMinutes(7))); // the hourly cap
        Assert.True(t.TryTake("a:leak", now.AddMinutes(61)));  // both the window and the hour have passed
    }

    [Fact]
    public void Text_is_short_and_in_russian()
    {
        var text = SmsText.Event("leak", "Кухня", DateTimeOffset.Parse("2026-11-14T23:14:00+03:00"));
        Assert.Equal("Улей КРИТИЧНО: Протечка, Кухня, 23:14", text);
        Assert.True(SmsText.Event("leak", new string('x', 300), DateTimeOffset.UtcNow).Length <= SmsText.MaxChars);
    }

    [Fact]
    public async Task Modem_sends_the_pdu_after_the_prompt()
    {
        var channel = new FakeChannel(
            "\r\nOK\r\n",                // ATE0
            "\r\n+CPIN: READY\r\n\r\nOK\r\n",
            "\r\nOK\r\n",                // CMGF
            "\r\n> ",                    // CMGS prompt
            "\r\n+CMGS: 12\r\n\r\nOK\r\n");
        var modem = new AtSmsModem(() => channel, Options.Create(new SmsOptions()), NullLogger<AtSmsModem>.Instance);

        await modem.SendAsync("+79161234567", "Привет", default);

        Assert.Equal(["ATE0\r", "AT+CPIN?\r", "AT+CMGF=0\r", "AT+CMGS=25\r",
            "00010" + "00B919761214365F7000" + "80C041F04400438043204350442\x1A"], channel.Written);
    }

    [Fact]
    public async Task Modem_error_is_thrown_and_a_pin_is_not_guessed()
    {
        var pin = new FakeChannel("\r\nOK\r\n", "\r\n+CPIN: SIM PIN\r\n\r\nOK\r\n");
        var modem = new AtSmsModem(() => pin, Options.Create(new SmsOptions()), NullLogger<AtSmsModem>.Instance);
        await Assert.ThrowsAsync<SmsException>(() => modem.SendAsync("+79161234567", "x", default));

        var rejected = new FakeChannel("\r\nOK\r\n", "\r\n+CPIN: READY\r\n\r\nOK\r\n", "\r\nOK\r\n", "\r\n> ", "\r\n+CMS ERROR: 330\r\n");
        var modem2 = new AtSmsModem(() => rejected, Options.Create(new SmsOptions()), NullLogger<AtSmsModem>.Instance);
        await Assert.ThrowsAsync<SmsException>(() => modem2.SendAsync("+79161234567", "x", default));
    }

    private sealed class FakeChannel(params string[] replies) : IAtChannel
    {
        private readonly Queue<string> _replies = new(replies);
        public List<string> Written { get; } = [];

        public Task WriteAsync(string data, CancellationToken ct)
        {
            Written.Add(data);
            return Task.CompletedTask;
        }

        public Task<string> ReadUntilAsync(string[] terminators, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : "");

        public void Dispose()
        {
        }
    }
}
