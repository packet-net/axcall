using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Packet.Ax25;

namespace Axlisten;

/// <summary>
/// One JSON object per line, for a program rather than a person.
/// </summary>
/// <remarks>
/// <para>
/// Fields are present only where the frame type carries them, as in the text
/// output: <c>ns</c> only on an I frame, <c>nr</c> only on I and S frames,
/// <c>pid</c> only where there is one. The information field is always there
/// as base64, because it is binary until proven otherwise, and additionally as
/// <c>text</c> when it reads as text. <c>decoded</c> carries the one-line
/// summaries of APRS, NET/ROM, IP or ARP that the text output shows.
/// </para>
/// <code>
/// {"time":"2026-09-14T18:45:51.123Z","port":"radio","kissPort":0,"from":"G4ABC-1","to":"APRS",
///  "via":[{"call":"WIDE1-1","repeated":true}],"type":"UI","cr":"command","pf":false,
///  "pid":240,"len":5,"info":"SGVsbG8=","text":"Hello"}
/// </code>
/// </remarks>
internal sealed class JsonFormatter : IFrameFormatter
{
    private readonly ArrayBufferWriter<byte> buffer = new(512);

    public string Format(MonitoredFrame frame)
    {
        buffer.ResetWrittenCount();
        // Relaxed, so "Zoë" is written as such rather than as \u00EB. Control
        // characters are still escaped, which is what keeps one frame one line.
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("time", frame.ReceivedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
            json.WriteString("port", frame.Port);
            json.WriteNumber("kissPort", frame.KissPort);

            if (frame.Frame is { } ax25)
                WriteFrame(json, ax25);
            else
            {
                json.WriteString("error", "unreadable frame");
                json.WriteBase64String("raw", frame.Bytes.Span);
            }

            if (frame.Radio is { } radio)
            {
                if (radio.RssiDbm is { } rssi) json.WriteNumber("rssi", rssi);
                if (radio.SnrDb is { } snr) json.WriteNumber("snr", snr);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static void WriteFrame(Utf8JsonWriter json, Ax25Frame frame)
    {
        json.WriteString("from", frame.Source.Callsign.ToString());
        json.WriteString("to", frame.Destination.Callsign.ToString());

        if (frame.Digipeaters.Count > 0)
        {
            json.WriteStartArray("via");
            foreach (var digi in frame.Digipeaters)
            {
                json.WriteStartObject();
                json.WriteString("call", digi.Callsign.ToString());
                json.WriteBoolean("repeated", digi.CrhBit);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }

        var type = frame.FrameType;
        json.WriteString("type", type.Mnemonic());

        if (frame.IsCommand) json.WriteString("cr", "command");
        else if (frame.IsResponse) json.WriteString("cr", "response");
        else json.WriteNull("cr");

        json.WriteBoolean("pf", frame.PollFinal);

        if (type is Ax25FrameType.I)
            json.WriteNumber("ns", frame.Ns);
        if (type.CarriesNr())
            json.WriteNumber("nr", frame.Nr);
        if (frame.IsExtendedControl)
            json.WriteBoolean("mod128", true);

        if (frame.Pid is byte pid)
            json.WriteNumber("pid", pid);

        var info = frame.Info.Span;
        json.WriteNumber("len", info.Length);
        if (info.Length > 0)
        {
            json.WriteBase64String("info", info);
            if (PayloadDecoder.TryText(info, out var lines))
                json.WriteString("text", string.Join('\n', lines));

            // The same one-line summaries the text output shows, for a program
            // that wants the gist without decoding APRS or NET/ROM itself.
            var decoded = PayloadDecoder.Describe(frame, alwaysHex: false).Where(l => l.Kind == LineKind.Summary).ToList();
            if (decoded.Count > 0)
            {
                json.WriteStartArray("decoded");
                foreach (var line in decoded)
                    json.WriteStringValue(line.Text.Trim());
                json.WriteEndArray();
            }
        }
    }
}
