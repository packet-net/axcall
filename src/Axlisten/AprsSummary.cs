using System.Globalization;
using System.Text;
using Packet.Aprs;
using Packet.Ax25;

namespace Axlisten;

/// <summary>
/// APRS as lines a person can read: what kind of report it is and the fields
/// that matter, then the free text on a line of its own.
/// </summary>
/// <remarks>
/// The decoding is Packet.Aprs's, which reads every APRS 1.2 data type and
/// never throws over an information field. A UI frame with PID F0 that is not
/// APRS, a beacon or a BBS's mail list, comes back unrecognised, and is left
/// to be shown as text.
/// </remarks>
internal static class AprsSummary
{
    public static bool Describe(Ax25Frame frame, List<PayloadLine> lines)
    {
        if (!AprsPacket.TryDecodeAx25(frame.ToBytes(), out var packet) || packet.Data is AprsUnrecognizedData)
            return false;

        DescribePacket(packet, lines, "");
        return true;
    }

    private static void DescribePacket(AprsPacket packet, List<PayloadLine> lines, string indent)
    {
        // The device database also names the generic destinations, APRS and
        // the experimental APZ range, as made by "Unknown". That says nothing.
        var device = AprsDeviceIdentification.Identify(packet);
        var from = device is { Vendor: { } vendor } && vendor != "Unknown" && device.ToString().Length > 0
            ? $" [{device}]"
            : "";

        switch (packet.Data)
        {
            case AprsObjectReport o:
                Summary(lines, indent, o.IsAlive ? $"APRS object {o.Name.Trim()} {Where(o)}{from}" : $"APRS object {o.Name.Trim()} killed{from}");
                Details(lines, indent, o);
                break;

            case AprsItemReport i:
                Summary(lines, indent, i.IsAlive ? $"APRS item {i.Name.Trim()} {Where(i)}{from}" : $"APRS item {i.Name.Trim()} killed{from}");
                Details(lines, indent, i);
                break;

            case AprsMicEReport m:
                Summary(lines, indent, $"APRS Mic-E {Where(m)}{from}");
                Details(lines, indent, m);
                break;

            case AprsPositionedData p:
                Summary(lines, indent, $"APRS position {Where(p)}{from}");
                Details(lines, indent, p);
                break;

            case AprsTextMessage msg:
                Summary(lines, indent, $"APRS message to {msg.Addressee.Trim()}{Id(msg.MessageId)}{from}");
                Text(lines, indent, msg.Text);
                break;

            case AprsMessageAck ack:
                Summary(lines, indent, $"APRS ack to {ack.Addressee.Trim()}{Id(ack.MessageId)}");
                break;

            case AprsMessageReject rej:
                Summary(lines, indent, $"APRS reject to {rej.Addressee.Trim()}{Id(rej.MessageId)}");
                break;

            case AprsBulletin bln:
                Summary(lines, indent, $"APRS bulletin {bln.Addressee.Trim()}{from}");
                Text(lines, indent, bln.Text);
                break;

            case AprsNwsBulletin nws:
                Summary(lines, indent, $"APRS weather bulletin {nws.Addressee.Trim()}");
                Text(lines, indent, nws.Text);
                break;

            case AprsDirectedQuery q:
                Summary(lines, indent, $"APRS query ?{q.QueryType} to {q.Addressee.Trim()}");
                break;

            case AprsGeneralQuery q:
                Summary(lines, indent, $"APRS query ?{q.QueryType}");
                break;

            case AprsStatusReport s:
                Summary(lines, indent, $"APRS status{from}");
                Text(lines, indent, s.Text);
                break;

            case AprsWeatherReport w:
                Summary(lines, indent, $"APRS weather {Weather(w.Weather)}{from}");
                Text(lines, indent, w.Comment);
                break;

            case AprsTelemetryReport t:
                var analog = string.Join(',', t.Analog.Select(a => a?.ToString(CultureInfo.InvariantCulture) ?? ""));
                var digital = t.Digital is { } d ? " " + Convert.ToString(d, 2).PadLeft(8, '0') : "";
                Summary(lines, indent, $"APRS telemetry #{t.Sequence} {analog}{digital}");
                Text(lines, indent, t.Comment);
                break;

            case AprsStationCapabilities c:
                Summary(lines, indent, "APRS capabilities " + string.Join(", ",
                    c.Capabilities.Select(x => x.Value is null ? x.Token : $"{x.Token}={x.Value}")));
                break;

            case AprsMaidenheadBeacon g:
                Summary(lines, indent, $"APRS grid {g.Locator}{from}");
                Text(lines, indent, g.Comment);
                break;

            case AprsThirdPartyTraffic relayed:
                // An IGate's relay: the real packet is inside, header and all.
                var inner = relayed.Packet;
                Summary(lines, indent, $"APRS third party {inner.Source}>{inner.Destination}");
                DescribePacket(inner, lines, indent + "  ");
                break;

            default:
                Summary(lines, indent, $"APRS {Kind(packet.Data)}{from}");
                break;
        }
    }

    // Position, symbol, then movement where there is any.
    private static string Where(AprsPositionedData p)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{p.Position.Latitude:0.0000} {p.Position.Longitude:0.0000}");
        if (p.Symbol.Description is { Length: > 0 } symbol)
            sb.Append(", ").Append(symbol);
        if (p.CourseDegrees is { } course && p.SpeedKnots is { } speed)
            sb.Append(CultureInfo.InvariantCulture, $", {course}° {speed:0.#} kn");
        else if (p.SpeedKnots is { } s)
            sb.Append(CultureInfo.InvariantCulture, $", {s:0.#} kn");
        if (p.AltitudeFeet is { } feet)
            sb.Append(CultureInfo.InvariantCulture, $", {feet:0} ft");
        return sb.ToString();
    }

    private static void Details(List<PayloadLine> lines, string indent, AprsPositionedData p)
    {
        if (p.Weather is { } weather)
            Summary(lines, indent, $"weather {Weather(weather)}");
        Text(lines, indent, p.Comment);
    }

    // In the units APRS sends, as Packet.Aprs keeps them.
    private static string Weather(AprsWeather w)
    {
        var parts = new List<string>();
        if (w.TemperatureFahrenheit is { } t) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{t}°F"));
        if (w.WindDirectionDegrees is { } dir && w.WindSpeedMph is { } speed)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"wind {dir}° {speed:0.#} mph"));
        else if (w.WindSpeedMph is { } s)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"wind {s:0.#} mph"));
        if (w.WindGustMph is { } gust) parts.Add(string.Create(CultureInfo.InvariantCulture, $"gust {gust} mph"));
        if (w.HumidityPercent is { } h) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{h}%"));
        if (w.PressureMillibars is { } mb) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{mb:0.0} mb"));
        if (w.RainLastHourInches is { } rain) parts.Add(string.Create(CultureInfo.InvariantCulture, $"rain {rain:0.00} in/h"));
        return parts.Count > 0 ? string.Join(", ", parts) : "(no readings)";
    }

    private static string Id(string? id) => string.IsNullOrEmpty(id) ? "" : $" #{id}";

    private static string Kind(AprsData data) => data switch
    {
        AprsNmeaReport { Position: { } p } => string.Create(CultureInfo.InvariantCulture, $"NMEA {p.Latitude:0.0000} {p.Longitude:0.0000}"),
        AprsNmeaReport => "NMEA",
        AprsRawWeatherReport => "raw weather",
        AprsAgreloDfReport df => string.Create(CultureInfo.InvariantCulture, $"DF bearing {df.BearingDegrees}°"),
        AprsUserDefinedData => "user-defined data",
        AprsTestData => "test data",
        _ => "data",
    };

    private static void Summary(List<PayloadLine> lines, string indent, string text)
        => lines.Add(new PayloadLine(indent + text, LineKind.Summary));

    // The station's own words, made safe the same way as any other text.
    private static void Text(List<PayloadLine> lines, string indent, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (PayloadDecoder.TryText(Encoding.UTF8.GetBytes(text), out var textLines))
        {
            foreach (var line in textLines)
                lines.Add(new PayloadLine(indent + line, LineKind.Text));
        }
    }
}
