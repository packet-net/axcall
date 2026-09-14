using AwesomeAssertions;
using Packet.Ax25.Transport;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// The KISS channel-access parameters: what kissparms(8) set per interface
/// under the kernel stack, which axcall has to set itself now that it holds the
/// only handle on the TNC.
/// </summary>
public sealed class ChannelParamsTests
{
    [Theory]
    // Milliseconds in, KISS 10 ms steps out, as kissparms took them.
    [InlineData("0", 0)]
    [InlineData("10", 1)]
    [InlineData("300", 30)]
    [InlineData("2550", 255)]
    public void Timers_Convert_Milliseconds_To_Kiss_Units(string ms, int expected)
    {
        ChannelParams.TryParseTimerMs(ms, out var units, out var error).Should().BeTrue();
        units.Should().Be((byte)expected);
        error.Should().BeNull();
    }

    [Theory]
    // Not a multiple of ten: refused rather than rounded, because keying 5 ms
    // later than asked is the sort of thing nobody notices.
    [InlineData("305")]
    [InlineData("1")]
    // Past what the single KISS byte can carry.
    [InlineData("2560")]
    [InlineData("-10")]
    [InlineData("notanumber")]
    [InlineData("")]
    public void Invalid_Timers_Are_Refused(string ms)
    {
        ChannelParams.TryParseTimerMs(ms, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("63", 63)]
    [InlineData("255", 255)]
    public void Persist_Is_Raw(string text, int expected)
    {
        ChannelParams.TryParsePersist(text, out var value, out _).Should().BeTrue();
        value.Should().Be((byte)expected);
    }

    [Theory]
    [InlineData("256")]
    [InlineData("-1")]
    [InlineData("notanumber")]
    public void Invalid_Persist_Is_Refused(string text)
    {
        ChannelParams.TryParsePersist(text, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Only_What_Was_Asked_For_Is_Sent()
    {
        // The point of the whole design: a TNC set up deliberately must not be
        // silently overridden just because axcall connected to it.
        var tnc = new RecordingTnc();
        await new ChannelParams(TxDelay: 30, Persist: null, SlotTime: null, TxTail: null)
            .ApplyAsync(tnc, CancellationToken.None);

        tnc.Sent.Should().Equal("txdelay=30");
    }

    [Fact]
    public async Task Nothing_Is_Sent_When_Nothing_Was_Asked_For()
    {
        var tnc = new RecordingTnc();
        await ChannelParams.None.ApplyAsync(tnc, CancellationToken.None);
        tnc.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task All_Four_Go_Out_In_Kiss_Command_Order()
    {
        var tnc = new RecordingTnc();
        await new ChannelParams(TxDelay: 30, Persist: 63, SlotTime: 10, TxTail: 2)
            .ApplyAsync(tnc, CancellationToken.None);

        // TXDELAY 0x01, P 0x02, SlotTime 0x03, TXTail 0x04.
        tnc.Sent.Should().Equal("txdelay=30", "persist=63", "slottime=10", "txtail=2");
    }

    [Fact]
    public void Override_Is_Per_Parameter()
    {
        var fromFile = new ChannelParams(TxDelay: 30, Persist: 63, SlotTime: null, TxTail: null);
        var fromFlags = new ChannelParams(TxDelay: null, Persist: 128, SlotTime: 5, TxTail: null);

        var merged = fromFile.OverriddenBy(fromFlags);

        merged.TxDelay.Should().Be(30, "the file value survives when no flag replaces it");
        merged.Persist.Should().Be(128, "the flag wins");
        merged.SlotTime.Should().Be(5, "a flag can add what the file did not set");
        merged.TxTail.Should().BeNull();
    }

    [Fact]
    public void Description_Reads_Back_In_The_Units_The_User_Gave()
    {
        new ChannelParams(30, 63, 10, 2).ToString()
            .Should().Be("txdelay 300 ms, persist 63, slottime 100 ms, txtail 20 ms");
    }

    /// <summary>Records what was sent, in order, rather than talking to a radio.</summary>
    private sealed class RecordingTnc : ICsmaChannelParams
    {
        public List<string> Sent { get; } = [];

        public Task SetTxDelayAsync(byte v, CancellationToken ct = default) { Sent.Add($"txdelay={v}"); return Task.CompletedTask; }
        public Task SetPersistenceAsync(byte v, CancellationToken ct = default) { Sent.Add($"persist={v}"); return Task.CompletedTask; }
        public Task SetSlotTimeAsync(byte v, CancellationToken ct = default) { Sent.Add($"slottime={v}"); return Task.CompletedTask; }
        public Task SetTxTailAsync(byte v, CancellationToken ct = default) { Sent.Add($"txtail={v}"); return Task.CompletedTask; }
    }
}
