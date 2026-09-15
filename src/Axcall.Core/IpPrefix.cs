using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Axcall;

/// <summary>
/// An IPv4 address range in CIDR form, and the one question worth asking of
/// one: does this address fall inside it.
/// </summary>
public readonly record struct IpPrefix(uint Network, int Length)
{
    /// <summary>Every address. Written "any" or "0.0.0.0/0".</summary>
    public static readonly IpPrefix Any = new(0, 0);

    /// <summary>224.0.0.0/4, the multicast range.</summary>
    public static readonly IpPrefix Multicast = new(0xE0000000, 4);

    /// <summary>169.254.0.0/16, link-local.</summary>
    public static readonly IpPrefix LinkLocal = new(0xA9FE0000, 16);

    /// <summary>The mask this length implies, in host order.</summary>
    public uint Mask => Length == 0 ? 0u : uint.MaxValue << (32 - Length);

    public bool Contains(uint address) => (address & Mask) == (Network & Mask);

    public bool Contains(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetwork && Contains(ToUInt32(address));

    /// <summary>
    /// A destination no station should be keying a transmitter for without
    /// saying so in as many words: multicast, the all-ones broadcast, and
    /// link-local.
    /// </summary>
    /// <remarks>
    /// These are the addresses a desktop operating system talks to on its own
    /// initiative, constantly, with nobody having asked: mDNS at
    /// 224.0.0.251, SSDP at 239.255.255.250, DHCP at 255.255.255.255, LLMNR,
    /// WS-Discovery. On a wired LAN that is background noise. On a shared
    /// channel it is a transmitter keying up unprompted under somebody's
    /// personal licence.
    /// </remarks>
    public static bool IsChatty(uint address)
        => Multicast.Contains(address)
        || LinkLocal.Contains(address)
        || address == uint.MaxValue;

    public static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out var written) && written == 4
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes)
            : throw new ArgumentException($"not an IPv4 address: {address}", nameof(address));
    }

    public static IPAddress ToAddress(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    /// <summary>
    /// Parse "10.0.0.0/8", a bare address (taken as /32), or "any".
    /// </summary>
    public static bool TryParse(string? text, out IpPrefix prefix, out string? error)
    {
        prefix = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "expected an address, an address/length, or 'any'";
            return false;
        }

        if (text.Equals("any", StringComparison.OrdinalIgnoreCase) || text == "0.0.0.0/0")
        {
            prefix = Any;
            return true;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? text : text[..slash];

        if (!IPAddress.TryParse(addressText, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            error = $"'{addressText}' is not an IPv4 address";
            return false;
        }

        int length = 32;
        if (slash >= 0)
        {
            if (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > 32)
            {
                error = $"'{text[(slash + 1)..]}' is not a prefix length in 0..32";
                return false;
            }
        }

        // Normalise: 44.131.20.7/24 means the same range as 44.131.20.0/24, and
        // silently meaning something other than what was typed is worse than
        // storing the range that was meant.
        var value = ToUInt32(address);
        prefix = new IpPrefix(length == 0 ? 0 : value & (uint.MaxValue << (32 - length)), length);
        return true;
    }

    public override string ToString()
        => Length == 0 ? "any" : $"{ToAddress(Network)}/{Length}";
}
