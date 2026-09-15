using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Axcall;
using Microsoft.Win32.SafeHandles;

namespace Axtun;

/// <summary>
/// A Linux TUN device: a network interface the kernel routes to, whose other
/// end is a file descriptor we read IP packets out of and write IP packets
/// into.
/// </summary>
/// <remarks>
/// <para>
/// TUN and not TAP. TAP would hand us Ethernet frames, which means fourteen
/// bytes of MAC header on every packet of a channel whose whole frame is a few
/// hundred bytes, in order to address a link that has no MAC addresses on it.
/// AX.25 is not Ethernet, and pretending otherwise costs six per cent of the
/// channel to say so.
/// </para>
/// <para>
/// A TUN device comes up NOARP, so the kernel never asks who owns an address
/// on this link: it hands the packet over and leaves the question to us. That
/// is why the route table in the config file is structural rather than an
/// optimisation.
/// </para>
/// </remarks>
public sealed partial class TunDevice : IDisposable
{
    /// <summary>The character device that hands out TUN and TAP interfaces.</summary>
    public const string ControlDevice = "/dev/net/tun";

    /// <summary>Longest interface name the kernel will take, not counting the terminator.</summary>
    public const int MaxNameLength = 15;

    private const short IffTun = 0x0001;
    private const short IffNoPi = 0x1000;
    private const short IffUp = 0x0001;
    private const short IffRunning = 0x0040;

    private const ulong Tunsetiff = 0x400454CA;
    private const ulong Siocsifaddr = 0x8916;
    private const ulong Siocgifaddr = 0x8915;
    private const ulong Siocsifnetmask = 0x891C;
    private const ulong Siocgifflags = 0x8913;
    private const ulong Siocsifflags = 0x8914;
    private const ulong Siocsifmtu = 0x8922;

    private const short PollIn = 0x001;
    private const int Eperm = 1;

    // struct ifreq: char ifr_name[IFNAMSIZ] followed by a union wide enough for
    // a sockaddr. Only one union member is in play per call, so it is read and
    // written a field at a time.
    private const int NameBytes = 16;
    private const int UnionBytes = 24;
    private const int IfReqBytes = NameBytes + UnionBytes;

    private readonly FileStream stream;
    private readonly int descriptor;

    private TunDevice(SafeFileHandle handle, string name)
    {
        descriptor = (int)handle.DangerousGetHandle();
        // bufferSize 0 keeps FileStream out of the way: a TUN device is
        // packet-oriented, one read is exactly one packet, and buffering would
        // glue two packets together or split one in half.
        stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0);
        Name = name;
    }

    /// <summary>The interface name the kernel settled on.</summary>
    public string Name { get; }

    /// <summary>
    /// Create or attach to a TUN interface.
    /// </summary>
    /// <remarks>
    /// Attaching to a persistent device created earlier by
    /// <c>ip tuntap add</c> works and needs no privilege beyond read and write
    /// on the control device, which is how to run this without root. Creating
    /// one needs CAP_NET_ADMIN.
    /// </remarks>
    /// <exception cref="TunException">The device could not be opened or created.</exception>
    [SupportedOSPlatform("linux")]
    public static TunDevice Open(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Length > MaxNameLength)
            throw new TunException($"interface name '{name}' is too long (at most {MaxNameLength} characters)");

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(ControlDevice, FileMode.Open, FileAccess.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TunException(
                $"cannot open {ControlDevice}: {ex.Message}. "
                + "The tun module has to be loaded and the device readable and writable.", ex);
        }

        Span<byte> request = stackalloc byte[IfReqBytes];
        SetName(request, name);
        SetFlags(request, IffTun | IffNoPi);

        if (Ioctl(handle, Tunsetiff, request) < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new TunException(
                error == Eperm
                    ? $"not permitted to create the interface '{name}'. Creating one needs "
                      + "CAP_NET_ADMIN: run as root, or create a persistent device first with "
                      + $"'ip tuntap add {name} mode tun user $USER' and then run unprivileged."
                    : $"cannot create the interface '{name}': {Describe(error)}");
        }

        return new TunDevice(handle, ReadName(request));
    }

    /// <summary>Give the interface an address and an MTU, and bring it up.</summary>
    /// <exception cref="TunException">Any of the steps failed.</exception>
    [SupportedOSPlatform("linux")]
    public void Configure(IPAddress address, int prefixLength, int mtu)
    {
        ArgumentNullException.ThrowIfNull(address);

        // A socket is the handle every interface ioctl wants. Which socket does
        // not matter, only its address family.
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var control = socket.SafeHandle;

        Span<byte> request = stackalloc byte[IfReqBytes];
        SetName(request, Name);

        SetAddress(request, address);
        if (Ioctl(control, Siocsifaddr, request) < 0)
            throw Failed($"set the address of {Name} to {address}");

        SetAddress(request, IpPrefix.ToAddress(new IpPrefix(0, prefixLength).Mask));
        if (Ioctl(control, Siocsifnetmask, request) < 0)
            throw Failed($"set the netmask of {Name} to /{prefixLength}");

        Union(request).Clear();
        BinaryPrimitives.WriteInt32LittleEndian(Union(request), mtu);
        if (Ioctl(control, Siocsifmtu, request) < 0)
            throw Failed($"set the MTU of {Name} to {mtu}");

        Union(request).Clear();
        if (Ioctl(control, Siocgifflags, request) < 0)
            throw Failed($"read the flags of {Name}");

        SetFlags(request, (short)(BinaryPrimitives.ReadInt16LittleEndian(Union(request)) | IffUp | IffRunning));
        if (Ioctl(control, Siocsifflags, request) < 0)
            throw Failed($"bring {Name} up");
    }

    /// <summary>
    /// The address currently on the interface, or null if it has none.
    /// </summary>
    /// <remarks>
    /// Used when the operator configured the interface themselves: we still
    /// have to know our own address in order to answer ARP for it.
    /// </remarks>
    [SupportedOSPlatform("linux")]
    public IPAddress? ReadAddress()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Span<byte> request = stackalloc byte[IfReqBytes];
        SetName(request, Name);

        if (Ioctl(socket.SafeHandle, Siocgifaddr, request) < 0)
            return null;

        var union = Union(request);
        return BinaryPrimitives.ReadInt16LittleEndian(union) == (short)AddressFamily.InterNetwork
            ? new IPAddress(union.Slice(4, 4))
            : null;
    }

    /// <summary>
    /// Wait until there is a packet to read, or the timeout expires.
    /// </summary>
    /// <remarks>
    /// Polling rather than a blocking read, because closing the descriptor
    /// under a thread already blocked in read(2) does not reliably wake it on
    /// Linux, and a tool that will not shut down is worse than one that wakes
    /// up a few times a second to check whether it should.
    /// </remarks>
    [SupportedOSPlatform("linux")]
    public bool WaitForPacket(int timeoutMilliseconds)
    {
        var poll = new PollFd { Fd = descriptor, Events = PollIn };
        int ready = Poll(ref poll, 1, timeoutMilliseconds);
        return ready > 0 && (poll.Revents & PollIn) != 0;
    }

    /// <summary>
    /// Read one IP packet. The buffer has to be at least the interface MTU: a
    /// short read loses the rest of the packet rather than continuing it.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public int Read(Span<byte> buffer) => stream.Read(buffer);

    /// <summary>Hand one IP packet to the kernel as if it had arrived on the interface.</summary>
    [SupportedOSPlatform("linux")]
    public void Write(ReadOnlySpan<byte> packet) => stream.Write(packet);

    public void Dispose() => stream.Dispose();

    private static Span<byte> Union(Span<byte> request) => request[NameBytes..];

    private static void SetName(Span<byte> request, string name)
    {
        request.Clear();
        Encoding.ASCII.GetBytes(name, request[..MaxNameLength]);
    }

    private static string ReadName(ReadOnlySpan<byte> request)
    {
        var name = request[..NameBytes];
        var end = name.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? name : name[..end]);
    }

    private static void SetFlags(Span<byte> request, short flags)
    {
        Union(request).Clear();
        BinaryPrimitives.WriteInt16LittleEndian(Union(request), flags);
    }

    /// <summary>Put a struct sockaddr_in in the union: family, port, address.</summary>
    private static void SetAddress(Span<byte> request, IPAddress address)
    {
        var union = Union(request);
        union.Clear();
        BinaryPrimitives.WriteInt16LittleEndian(union, (short)AddressFamily.InterNetwork);
        address.TryWriteBytes(union[4..], out _);
    }

    private static TunException Failed(string what)
        => new($"cannot {what}: {Describe(Marshal.GetLastPInvokeError())}");

    private static string Describe(int error) => new Win32Exception(error).Message;

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(SafeHandle fd, ulong request, Span<byte> argp);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int Poll(ref PollFd fds, nuint count, int timeoutMilliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }
}

/// <summary>Something went wrong setting up or using the TUN device.</summary>
public sealed class TunException : Exception
{
    public TunException(string message) : base(message) { }

    public TunException(string message, Exception inner) : base(message, inner) { }
}
