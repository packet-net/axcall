using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Axcall.Tests;

/// <summary>
/// Unit coverage for the receive-path line-ending translation
/// (<see cref="SessionRelay.RenderReceivedText"/>). Packet data is
/// CR-terminated; the terminal needs LF to advance a line.
/// </summary>
public sealed class LineEndingTests
{
    [Theory]
    // Lone CR (the dominant packet convention) becomes LF.
    [InlineData("hello\r", "hello\n")]
    // CRLF collapses to a single LF (not a double break).
    [InlineData("hello\r\n", "hello\n")]
    // Multiple CR-terminated lines each become LF.
    [InlineData("one\rtwo\rthree\r", "one\ntwo\nthree\n")]
    // Multiple CRLF lines.
    [InlineData("one\r\ntwo\r\n", "one\ntwo\n")]
    // Mixed CRLF and lone CR.
    [InlineData("a\r\nb\rc", "a\nb\nc")]
    // CR in the middle of a line still breaks.
    [InlineData("left\rright", "left\nright")]
    // No terminator: passes through, no spurious newline appended (prompts).
    [InlineData("cmd: ", "cmd: ")]
    // Bare LF is left untouched.
    [InlineData("a\nb", "a\nb")]
    // Empty payload.
    [InlineData("", "")]
    public void RenderReceivedText_Translates_Cr_To_Lf(string input, string expected)
    {
        var rendered = SessionRelay.RenderReceivedText(Encoding.UTF8.GetBytes(input));
        rendered.Should().Be(expected);
    }

    [Fact]
    public void RenderReceivedText_Preserves_Utf8_Content()
    {
        var rendered = SessionRelay.RenderReceivedText(Encoding.UTF8.GetBytes("73 de M0LTE — café\r"));
        rendered.Should().Be("73 de M0LTE — café\n");
    }

    [Fact]
    public void RenderReceivedText_Does_Not_Append_Trailing_Newline_When_Absent()
    {
        var rendered = SessionRelay.RenderReceivedText(Encoding.UTF8.GetBytes("partial line no terminator"));
        rendered.Should().NotEndWith("\n");
    }
}
