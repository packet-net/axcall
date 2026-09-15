# IP over AX.25: what a real implementation actually does

Everything in this document is an observation against LinBPQ 6.0.25.28 on a simulated 1200 baud AFSK channel, not a reading of a specification. Where it contradicts a man page, the man page is not wrong so much as incomplete, and the observation is what `axtun` is built to.

The reproducible half lives in `tests/Axcall.Tests/Integration/IpOverAx25Tests.cs`, which runs against the same LinBPQ container the connected-mode tests use. The rest was measured by hand and is recorded here because it shaped the design.

## The setup

`net-sim` carries an AFSK1200 channel between two nodes. LinBPQ sits on one of them with its IP gateway enabled; the tests sit on the other. LinBPQ is `PN0TST` at 44.131.20.2, the tests are `AXTEST` at 44.131.20.1, and LinBPQ has a static ARP entry pointing 44.131.20.1 at `AXTEST` in datagram mode.

Three things about LinBPQ's IP gateway are not obvious and cost an afternoon each:

**`ADAPTER` is not optional.** `Init_IP` returns early and leaves IP support disabled if no adapter is configured, even for a node doing nothing but IP over AX.25. So the container needs an Ethernet interface to pcap, and therefore `CAP_NET_RAW`.

**It then insists on a TAP device.** On Linux, having found the adapter's address, it sets `WantTAP` and opens `/dev/net/tun`. That TAP is what carries a packet addressed to the node itself up to the container's own kernel, which is what makes the node answer a ping. So the container also needs `CAP_NET_ADMIN` and `/dev/net/tun`.

**`NoDefaultRoute` stops it answering.** With it set, LinBPQ does not add the 44/9 and 44.128/10 routes via its TAP, the container's kernel has no route back towards the radio side, and the echo reply is never generated. The packet arrives, is NATted to the container's own address, is delivered over the TAP, and nothing comes back. Leaving the routes in place fixes it.

None of that is `axtun`'s problem, but all of it is in the way of testing `axtun`, so it is written down in the fixture and in `bpq32.cfg`.

## What was measured

### IP in a UI frame with PID 0xCC works in both directions

A UI frame from `AXTEST` to `PN0TST` with PID 0xCC and an ICMP echo request for 44.131.20.2 gets an echo reply back in a UI frame with PID 0xCC. That is the whole datagram path against a real IP stack: LinBPQ's gateway, its TAP, the container's kernel, and back.

The same holds for routing. A packet from an off-net source to an address LinBPQ has an ARP entry for comes back on the air with the TTL decremented and the header checksum recomputed, which is a correct IP router doing its job.

### LinBPQ silently drops any frame over 328 bytes

`kiss.c` discards a KISS frame longer than 329 bytes, including the KISS type byte:

```c
if (len > 329)          // Max ax.25 frame + KISS Ctrl
{
    if (Port->Portvector)
        Debugprintf("BPQ32 overlong KISS frame - len = %d Port %d", ...);
    return 0;
}
```

The `Debugprintf` goes to a log file that is not written unless something else has already opened it, so in practice this is silent. A 544-byte frame was carried to LinBPQ's port by the channel simulator and produced nothing at all: no reply, no error, no log line.

So the largest AX.25 frame worth sending is **328 bytes**, which is 312 bytes of payload with no digipeaters and seven fewer for each one in the path. `Ax25Ip.MaxFrameBytes` is that number, and the interop test sends one frame over the limit, gets silence, then sends one under it and gets an answer, so that the silence is evidence rather than a dead link.

### Splitting is IP fragmentation, not AX.25 segmentation

`Ax25Pid.Segment = 0x08` is the NOS segmentation PID, and LinBPQ implements it on receive. It does not use it to send.

Given a 300-byte IP packet to route, LinBPQ emits two UI frames with PID 0xCC:

| | total length | MF | offset |
|---|---|---|---|
| first | 252 | set | 0 |
| second | 68 | clear | 232 |

252 is `(256 - 20) & ~7` plus the 20-byte header: a hardcoded 256 in `SendIPtoAX25`, rounded down to an eight-byte boundary. The PACLEN configured on the port is 120 and is ignored.

This is the convenient answer. A TUN device hands fragments to the kernel and the kernel reassembles them, so `axtun` implements nothing for this case.

### The ARP protocol type field is disputed and unchecked

AX.25 ARP is ordinary RFC 826 ARP with callsigns where the hardware addresses go: hardware type 3, hardware length 7, protocol length 4, thirty bytes, in a UI frame with PID 0xCD.

The protocol type field is filled in differently by the two implementations that matter:

- The Linux kernel sends **0x0800**, `ETH_P_IP`, because the generic ARP code fills the field in from the protocol rather than from anything AX.25 specific.
- LinBPQ sends **0x00CC**, the AX.25 PID for IP widened to sixteen bits, which is also what the NOS-derived stacks do.

Neither checks it. `ProcessAXARPMsg` dispatches on the operation code alone, and for a request addressed to its own address it mutates the message in place and sends it back, so the reply carries whatever the request used. Asking it with one of each and reading the replies confirms this, and that is a test.

`axtun` therefore sends 0x0800, accepts anything, and reflects what a request used.

### LinBPQ's virtual-circuit IP transmission is broken on 64-bit

Set an ARP entry to mode V (`ARP 44.131.20.3 AXVCT 2 V`), hand LinBPQ a packet for that address, and it does the right thing at first: it opens an AX.25 session to `AXVCT` with a SABM, and on receiving a UA it sends an I-frame. The I-frame is malformed.

Observed, with the address field and control byte separated out:

```
82b0ac86a840e0 a09c60a8a6a861 10 | a6 a8 61 03 cc 45000024...
AXVCT          PN0TST         I  | four bytes too many
```

The PID should be the single byte `cc`. Instead the payload begins with `a6 a8 61 03 cc`: the last two callsign octets of the source address, its SSID octet, the UI control byte, and then the PID. Four extra bytes, which is exactly the difference between a four-byte and an eight-byte pointer.

`SendNetFrame` in `IPCode.c` indexes the frame buffer at fixed offsets:

```c
memcpy(&Block[7],  ToCall,   7);
memcpy(&Block[14], FromCall, 7);
...
memcpy(&buffptr->PID, &Block[22], Len);
```

Those offsets are correct only if the `CHAIN` pointer at the head of the buffer is four bytes, which it was on 32-bit. On 64-bit every field is four bytes further along, so the copy starts inside the source callsign. The datagram path goes through `Send_AX_Datagram`, which uses struct members and is correct on any architecture, which is why only the VC path is affected.

This is worth reporting to G8BPQ. For our purposes it means the "accept VC as well as datagram" requirement is proven on our side by unit test and by reading LinBPQ's receive path, which queues PID 0xCC from an I-frame before any session handling:

```c
if (Buffer->PID == 0xCC || Buffer->PID == 0xCD)
{
    Q_IP_MSG((MESSAGE *)Buffer);
    return;
}
```

but it could not be observed end to end on the air, because nothing is currently sending it correctly.

### Van Jacobson compression is not universal

LinBPQ's layer 2 sends PID 0xCC, 0xCD and 0x08 to its IP stack and nothing else. There is no handling of 0x06 or 0x07 anywhere in it.

So VJ header compression belongs to JNOS and the NOS-derived stacks, not to the installed base generally. `axtun` does not implement it, and the man page says so rather than leaving it to be discovered.

## What that made axtun do

**Transmit datagram, accept both.** Out in UI frames with PID 0xCC, because running IP inside a reliable ARQ link puts AX.25's T1 and TCP's RTO on the same loss and wrecks the round-trip estimate. In from either, because `ax25rtd.conf(5)` has a per-route mode of datagram or virtual connect and both are configured in the field.

**A static route table, because a TUN device is NOARP.** The kernel hands the packet over and never asks who owns the address, so the map is the whole of how a packet finds a station rather than a cache in front of discovery.

**Answer ARP anyway, and remember what answers.** Otherwise nobody who has not hand-configured us can reach us. A station heard on the air is remembered for an hour with the reverse of the digipeater path it arrived by, so anything behind a digi becomes reachable without being written down. The config file always wins over anything heard.

**An MTU of 236.** Under everything measured, with room for a couple of digipeaters, and at 1200 baud the last twenty bytes are not what is slowing anything down.

**Default-deny egress.** Not from anything LinBPQ does; from what a Linux box does when you give it an interface. See `axtun(1)`.

## Proof that it works

With `axtun` on a TUN device at 44.131.20.1/24 and LinBPQ at 44.131.20.2, over the simulated 1200 baud channel:

```
$ ping -c 3 44.131.20.2
64 bytes from 44.131.20.2: icmp_seq=1 ttl=63 time=2781 ms
64 bytes from 44.131.20.2: icmp_seq=2 ttl=63 time=3771 ms
64 bytes from 44.131.20.2: icmp_seq=3 ttl=63 time=3979 ms
3 packets transmitted, 3 received, 0% packet loss
```

The other way, from inside the LinBPQ container:

```
$ ping -c 3 44.131.20.1
3 packets transmitted, 3 received, 0% packet loss
rtt min/avg/max/mdev = 2266.436/2366.570/2470.654/83.418 ms
```

And a TCP session, to LinBPQ's own web server, through its NAT, over the radio link:

```
$ curl -w '%{http_code}, %{size_download} bytes in %{time_total}s\n' http://44.131.20.2:8008/
200, 117 bytes in 15.804638s
```

Two and a half to four seconds for a ping is about right: two frames of roughly 90 bytes each at 1200 baud, plus TXDELAY at each end. Sixteen seconds for a 117-byte HTTP response is the three-way handshake, the request, the response and the teardown, on a half-duplex channel where every turn costs a transmission.

With no route configured at all, the first ping is lost and the rest succeed: that is the ARP request going out, LinBPQ answering it, and `axtun` learning the station. Which is the behaviour that was designed, doing what it was designed to do.
