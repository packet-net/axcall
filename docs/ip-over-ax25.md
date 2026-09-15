# IP over AX.25: what one implementation actually does

Everything in this document is an observation against **LinBPQ 6.0.25.28**, on a simulated 1200 baud AFSK channel, not a reading of a specification. Where it contradicts a man page, the man page is not wrong so much as incomplete, and the observation is what `axtun` is built to.

## Read this first: one peer is not the field

LinBPQ is one implementation and is treated here as the reference only because it was the one already running in this repo's test harness. That is a convenience, not a judgement about its correctness, and the work below turned up a real 64-bit bug in it, which is reason enough not to assume the rest of it is right either.

**XRouter** and the **Linux kernel's own AX.25 stack** have since been tested; see the sections at the end. **JNOS** has not. Where LinBPQ's behaviour has been turned into a number in the code, it is a default that can be changed rather than a limit that is enforced, and the comment beside it says which implementation it came from.

Three specific things to be suspicious of until they are tested more widely:

- **The 328-byte frame ceiling** is LinBPQ's KISS receive limit and nothing else. Another stack may take more, or less. `--mtu` will go above it and warns rather than refusing.
- ~~**The ARP protocol type we send** (0x0800) is read from the Linux kernel's generic ARP path, not observed.~~ **Measured, and it was wrong.** See below: every implementation sends 0x00CC and the kernel refuses anything else. Fixed.
- ~~**"VJ compression is a NOS thing"** rests on LinBPQ not implementing it.~~ **Checked against JNOS itself.** See below: it is not an AX.25 feature in JNOS either.

A proper multi-implementation interop campaign is tracked in #53. Run it with `scripts/peer-interop.sh`; it is deliberately not in CI, for reasons that are about capability rather than cost. See the peer suite note at the end.

## Where the evidence lives

The reproducible half is in `tests/Axcall.Tests/Integration/IpOverAx25Tests.cs`, which runs against the same LinBPQ container the connected-mode tests use. The rest was measured by hand and is recorded here because it shaped the design.

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

*One implementation. Not a property of AX.25, and not tested elsewhere.*

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

So the largest frame **LinBPQ** will take is **328 bytes**, which is 312 bytes of payload with no digipeaters and seven fewer for each one in the path. Whether that is the largest frame worth sending in general is exactly the open question. `Ax25Ip.MaxFrameBytes` is that number, and the interop test sends one frame over the limit, gets silence, then sends one under it and gets an answer, so that the silence is evidence rather than a dead link.

### Splitting is IP fragmentation, not AX.25 segmentation

`Ax25Pid.Segment = 0x08` is the NOS segmentation PID, and LinBPQ implements it on receive. It does not use it to send.

Given a 300-byte IP packet to route, LinBPQ emits two UI frames with PID 0xCC:

| | total length | MF | offset |
|---|---|---|---|
| first | 252 | set | 0 |
| second | 68 | clear | 232 |

252 is `(256 - 20) & ~7` plus the 20-byte header: a hardcoded 256 in `SendIPtoAX25`, rounded down to an eight-byte boundary. The PACLEN configured on the port is 120 and is ignored.

This is the convenient answer. A TUN device hands fragments to the kernel and the kernel reassembles them, so `axtun` implements nothing for this case.

### The ARP protocol type field is not what RFC 826 suggests, and the kernel enforces it

AX.25 ARP is ordinary RFC 826 ARP with callsigns where the hardware addresses go: hardware type 3, hardware length 7, protocol length 4, thirty bytes, in a UI frame with PID 0xCD.

**This is the one place a guess shipped as a bug, so it is worth reading in full.**

The field takes one of two values, and the answer is not the obvious one.

- **0x00CC**, the AX.25 PID for IP widened to sixteen bits. *Observed on the air from all three implementations tested:* LinBPQ, XRouter, and the Linux kernel's own AX.25 stack.
- **0x0800**, `ETH_P_IP`, which is what RFC 826 and the kernel's generic ARP code would lead you to expect. *Observed from nothing.*

The first version of `axtun` sent 0x0800, on the strength of reading the kernel's generic ARP path and reasoning that the field is filled in from the protocol rather than from anything AX.25 specific. That reading was half right and the conclusion was wrong: `arp_create` has an explicit `ARPHRD_AX25` case that overrides the generic behaviour with `AX25_P_IP`, and `arp_process` has the matching check on receive.

Measured directly, by asking a kernel peer for its own address twice:

```
-- who has 44.131.20.10, protocol type 0x0800 (what axtun sent)
   -> 0 frames back
-- who has 44.131.20.10, protocol type 0x00CC
   RX: AXKERN>AXPRB pid=0xcd  reply
```

So a Linux AX.25 station would **never** have answered an ARP request from `axtun`, silently. LinBPQ and XRouter would have, because neither checks the field, which is exactly why testing against them could not have caught it.

`axtun` now sends 0x00CC, accepts either, and reflects what a request used. The reflection matters for the same reason it always did: it cannot be wrong about a field it does not choose.

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

### LinBPQ has no Van Jacobson compression

LinBPQ's layer 2 sends PID 0xCC, 0xCD and 0x08 to its IP stack and nothing else. There is no handling of 0x06 or 0x07 anywhere in it.

That was as far as this went for a while, and the standing assumption was that VJ belonged to JNOS and the NOS-derived stacks. It does not. See the JNOS section at the end.

## What that made axtun do

**Transmit datagram, accept both.** Out in UI frames with PID 0xCC, because running IP inside a reliable ARQ link puts AX.25's T1 and TCP's RTO on the same loss and wrecks the round-trip estimate. In from either, because `ax25rtd.conf(5)` has a per-route mode of datagram or virtual connect and both are configured in the field.

**A static route table, because a TUN device is NOARP.** The kernel hands the packet over and never asks who owns the address, so the map is the whole of how a packet finds a station rather than a cache in front of discovery.

**Answer ARP anyway, and remember what answers.** Otherwise nobody who has not hand-configured us can reach us. A station heard direct is remembered for an hour, so it becomes reachable without being written down, and the config file always wins over anything heard. A station heard through a repeater is delivered upward but not remembered: nothing here digipeats, so a reply sent direct would not reach it, and recording it would create a station that looks known and is not.

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

---

# The Linux kernel's AX.25 stack

*Run 2026-09-15 against Debian 12, kernel 6.1.0-53-amd64, ax25-tools 0.0.10, on the ax25-lab VM. `kissattach` onto a socat pty bridged to the same net-sim channel.*

This cannot run in CI and never will: `AF_AX25` is refused inside a non-init user namespace, so every container on this project's infrastructure, including the GitHub Actions runner, gets `EAFNOSUPPORT` however many capabilities it is granted. It needs a VM. See #53.

## It settled the ARP protocol type, against us

Covered above. The short version: the kernel is the only implementation that **checks** the field, it requires `AX25_P_IP` (0x00CC), and axtun was sending `ETH_P_IP` (0x0800). A Linux station would never have answered an ARP request from it.

## The fix, measured end to end

Same lab, same kernel station, same channel, cold start with the neighbour cache flushed and nothing learned on either side. The only variable is the axtun binary.

| | ARP answered | ping |
|---|---|---|
| before the fix (0x0800) | no | **0 of 5** |
| v0.8.0 (0x00CC) | yes | **3 of 4** |

The one lost packet in the second row is the ARP round trip, which is the designed behaviour: the first packet to an unknown station is dropped while the request goes out, and whatever sent it retries.

```
axtun: who has 44.131.20.10? asked QST
axtun: told AXKERN that 44.131.20.30 is AXTUN
64 bytes from 44.131.20.10: icmp_seq=3 ttl=64 time=3696 ms
64 bytes from 44.131.20.10: icmp_seq=4 ttl=64 time=4194 ms
```

Both halves of the ARP contract are exercised there: axtun asking and being answered, and axtun answering the kernel's own request for its address.

## A note on chasing this one down

The failing case took far longer to diagnose than it should have, and the reason is worth recording. The frames were captured off the air and replayed byte for byte from a separate KISS client, which the kernel answered, appearing to exonerate the frame. That produced an hour of hypotheses about timing, cache state and host layout, all of them wrong.

What settled it was capturing the KISS bytes on the TCP connection rather than the decoded frames on the air, which showed `0003 0800` where the monitor had shown `0003 00cc`: the two runs were different binaries, one built before the fix and one after. **The monitor was telling the truth about a different process than the one under test.**

The lesson is not about ARP. It is that "I replayed the exact bytes and it worked" is only as good as the assumption that both runs came from the same build, and nothing ever checked that.

---

# XRouter

*Run 2026-09-15 against `ghcr.io/packethacking/xrouter:latest`, version 505c, on the same net-sim channel.*

XRouter's only KISS-capable interface type is `ASYNC`. `IFACES(6)` lists AGW, ASYNC, AXIP, AXTCP, AXUDP, EXTERNAL, LOOPBACK, TCP, TUN, UDP and YAM, and `EXTERNAL` is Ethernet, so it needs the same socat pty bridge the kernel does.

It answers AX.25 ARP and ICMP with axtun's codec unchanged, first attempt, and unlike LinBPQ it answers ping itself rather than through a TAP device and a NAT to the container's own kernel.

Two things worth knowing:

- **It sends ARP protocol type 0x00CC**, agreeing with LinBPQ and the kernel.
- **It supports `v = Virtual circuit (ip-over-ax25)` as a route mode**, alongside `d = Datagram`, and gets it right. See below.

Config notes: `LOCATOR` is mandatory or it exits 255. Forwarding to a third party needs an explicit `ip route add` as well as an `arp add`; the ARP entry alone is enough for XRouter to answer for its own address but not to route. And an `ip route add ... d` alongside an `arp add` makes it answer for **its own** address twice, which is worth knowing before writing a test that counts replies.

## The virtual-circuit row, closed

`axtun` accepts IP in connected-mode I-frames as well as in UI frames, because `ax25rtd.conf(5)` has a per-route mode of datagram or virtual connect and both are configured in the field. Until now that claim rested on a unit test and on reading LinBPQ's receive path. Nothing had ever sent us one: LinBPQ's virtual-circuit transmit is malformed on 64-bit, and the kernel leg needs a VM.

XRouter sends it, and sends it correctly. Given a route in mode `v`, it opens an AX.25 session to the target station and delivers the packet inside an I-frame:

```
XR0TST>AXVCT ctl=0x00 pid=0xcc
45000028 1234 0000 3f 01 ... 2c836309 -> 2c831401
```

Control 0x00 is an I-frame with N(S)=0, the PID is in the right place, the IP packet begins immediately after it, and the TTL has been decremented from 64 to 63 by a router doing its job. Compare LinBPQ's, four sections up, which puts four bytes of the previous UI header where the PID belongs.

This is now a test: `XrouterInteropTests.Ip_Arrives_Inside_A_Connected_Session_When_The_Peers_Route_Says_Virtual_Circuit`. It answers XRouter's call with the production `Ax25Listener`, takes the frame off `FrameTraced`, and runs it through the same `Ax25Ip.TryGetPayload` the tool uses. The same fixture covers the datagram encapsulation and the ARP protocol type in the same run, so all three of XRouter's behaviours are checked against one node.

**Both claims that rested on reading rather than observation are now settled.** One of them, the ARP protocol type, was wrong. This one was right.

---

# Running the peer suite

```sh
scripts/peer-interop.sh
```

XRouter runs in a container and is covered. The Linux kernel needs the `ax25-lab` VM, because `AF_AX25` is refused inside a non-init user namespace and every container here is one; that leg is manual, and its results are the kernel section above. JNOS is not yet written.

**This suite is on demand, and the reason is capability rather than cost.** A peer suite CI could run in full does not exist, because the kernel leg cannot run on the runner at any price. Given that, running the containerised half nightly buys little: the peers change about once a year and our code changes daily.

The failure mode of any on-demand suite is that nobody runs it and it rots, and the first person to try after six months cannot tell a regression from bit-rot in the harness. The mitigation here is that every run dates its findings in this document, so a stale result is visibly stale rather than silently assumed.

---

# JNOS, and the Van Jacobson compression question

*Checked 2026-09-15 against JNOS 2.0p.6 (February 2025), read from source rather than run.*

The interop table carried a row saying Van Jacobson header compression (PID 0x06) was needed for "JNOS and NOS-derived stacks", and that without it "a compressed peer is unintelligible, and we waste the channel". That row came from the AX.25 specification, which does define PIDs 0x06 and 0x07 for compressed and uncompressed Van Jacobson TCP/IP, plus the general knowledge that NOS-derived software does VJ.

**JNOS does not do Van Jacobson compression over AX.25.** It does VJ, and it has the full RFC 1144 implementation in `slhc.c`, but that is wired to SLIP and PPP and to nothing else:

```
$ grep -rln "slhc_compress|slhc_uncompress" --include=*.c .
ppp.c
slip.c
slhc.c
```

No AX.25 source file references it. And `ax25.h`'s PID list has no entry for 0x06 or 0x07 at all:

```c
#define PID_X25     0x01    /* CCITT X.25 PLP */
#define PID_SEGMENT 0x08    /* Segmentation fragment */
...
#define PID_IP      0xcc    /* ARPA Internet Protocol */
#define PID_ARP     0xcd    /* ARPA Address Resolution Protocol */
```

So the row named the one implementation that was supposed to prove it, and that implementation does not do it.

## Where that leaves the row

Three implementations inspected or tested, none of which does VJ over AX.25:

| | Van Jacobson over AX.25 |
|---|---|
| LinBPQ 6.0.25.28 | not implemented; dispatches 0xCC, 0xCD and 0x08 only |
| JNOS 2.0p.6 | not implemented for AX.25; VJ is SLIP and PPP only |
| XRouter 505c | no mention of compression in any of its manuals |

`axtun` does not implement it, and that is now a finding rather than a scope decision. The row is closed.

## Why it probably never happened

*Reasoning, not observation, and flagged as such.*

VJ compression encodes each header as a delta against the previous packet on the connection. That is why it wins so much: a 40-byte TCP/IP header becomes about five bytes. It also means a lost packet desynchronises the decompressor until an uncompressed header resynchronises it.

On a wire running SLIP or PPP that is a fine trade. On a half-duplex radio channel carrying IP in **UI frames**, which is unacknowledged by construction and the encapsulation everything here actually uses, losing packets is the normal case rather than the exception. The compression would spend on recovery most of what it saved.

That would make VJ over AX.25 a virtual-circuit-only feature, and virtual circuit is the minority mode. A feature that only works in the mode most people do not use, on a link layer where the failure mode is silent corruption of the next several packets, is a plausible thing for three independent implementations to have each decided against.

## What was not done

JNOS was read, not run. Building and running it was the plan, and the source answered the question before that was necessary: there is no code path to test. Its home at langelaar.net was unreachable throughout, so the source came from the [Rhizomatica mirror](https://github.com/Rhizomatica/jnos2), last updated March 2025.

If JNOS is ever wanted as a live peer for something else, the build is `make clean ; ./configure ; make` and the mirror is current.
