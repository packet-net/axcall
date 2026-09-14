using System.Globalization;
using Packet.Ax25.Transport;

namespace Axcall;

/// <summary>
/// The KISS channel-access parameters, held in the units the TNC wants: the
/// three timers in steps of 10 ms, persistence raw.
/// </summary>
/// <remarks>
/// <para>
/// These are not link parameters. Paclen, window and the timers negotiated with
/// the peer belong to one connection; these belong to the radio channel, and
/// govern how the TNC decides when it is allowed to key up. Under the kernel
/// stack they were per-interface and a separate tool, kissparms(8), set them
/// once for every program using the port. axcall now holds the only handle on
/// the TNC, so if it does not set them, nothing does.
/// </para>
/// <para>
/// Null means "not asked for", and nothing is sent. That matters: a TNC that
/// has been set up deliberately should not be quietly overridden just because
/// axcall connected to it.
/// </para>
/// </remarks>
internal sealed record ChannelParams(byte? TxDelay, byte? Persist, byte? SlotTime, byte? TxTail)
{
    internal static readonly ChannelParams None = new(null, null, null, null);

    public bool Any => TxDelay is not null || Persist is not null || SlotTime is not null || TxTail is not null;

    /// <summary>Per-parameter override: anything set on this wins, the rest falls through.</summary>
    public ChannelParams OverriddenBy(ChannelParams other) => new(
        other.TxDelay ?? TxDelay,
        other.Persist ?? Persist,
        other.SlotTime ?? SlotTime,
        other.TxTail ?? TxTail);

    /// <summary>The largest timer the KISS byte can express: 255 steps of 10 ms.</summary>
    internal const int MaxTimerMs = 2550;

    /// <summary>
    /// A timer in milliseconds, as kissparms(8) took them. The wire unit is
    /// 10 ms, so a value that is not a multiple of ten cannot be sent; that is
    /// refused rather than rounded, because silently keying 20 ms later than
    /// asked is the sort of thing nobody ever notices.
    /// </summary>
    internal static bool TryParseTimerMs(string text, out byte tenMsUnits, out string? error)
    {
        tenMsUnits = 0;
        error = null;

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
        {
            error = $"'{text}' is not a whole number of milliseconds";
            return false;
        }
        if (ms % 10 != 0)
        {
            error = $"{ms} ms is not a multiple of 10 (the KISS wire unit is 10 ms)";
            return false;
        }
        if (ms > MaxTimerMs)
        {
            error = $"{ms} ms is above the maximum of {MaxTimerMs} ms";
            return false;
        }

        tenMsUnits = (byte)(ms / 10);
        return true;
    }

    /// <summary>Persistence, 0 to 255 raw, as kissparms(8) took it.</summary>
    internal static bool TryParsePersist(string text, out byte value, out string? error)
    {
        value = 0;
        error = null;

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p > 255)
        {
            error = $"'{text}' is not a whole number in 0..255";
            return false;
        }

        value = (byte)p;
        return true;
    }

    /// <summary>
    /// Send whatever was asked for, in KISS command order (TXDELAY, P,
    /// SlotTime, TXTail). Only the parameters that were given are sent.
    /// </summary>
    internal async Task ApplyAsync(ICsmaChannelParams target, CancellationToken ct)
    {
        if (TxDelay is { } txDelay) await target.SetTxDelayAsync(txDelay, ct).ConfigureAwait(false);
        if (Persist is { } persist) await target.SetPersistenceAsync(persist, ct).ConfigureAwait(false);
        if (SlotTime is { } slotTime) await target.SetSlotTimeAsync(slotTime, ct).ConfigureAwait(false);
        if (TxTail is { } txTail) await target.SetTxTailAsync(txTail, ct).ConfigureAwait(false);
    }

    /// <summary>What was sent, for the status line, in the units the user gave.</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (TxDelay is { } d) parts.Add(FormattableString.Invariant($"txdelay {d * 10} ms"));
        if (Persist is { } p) parts.Add(FormattableString.Invariant($"persist {p}"));
        if (SlotTime is { } s) parts.Add(FormattableString.Invariant($"slottime {s * 10} ms"));
        if (TxTail is { } t) parts.Add(FormattableString.Invariant($"txtail {t * 10} ms"));
        return string.Join(", ", parts);
    }
}
