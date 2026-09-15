# Reaching the packet network without writing C#

Status: design notes, nothing implemented. Written 2026-09-14, after axcall 0.4.0 and the byte-transparency work in #39.

## The question

The kernel AX.25 stack gave every program on the machine a way in: `AF_AX25` sockets and `ax0` interfaces. Anything could use it, in any language, with no library. That is gone, and what replaced it is a set of .NET libraries. If you write C#, you are well served. If you do not, you are not served at all.

There are two answers to that, and they are not variations on one idea. They serve different people and they should be judged separately.

**Path A, the stream proxy**, serves people writing applications. It says: an AX.25 session is a byte stream, a TCP socket is a byte stream, so put a socket in front of it and every language is invited.

**Path B, the TUN device**, serves people who want software that already exists to work over radio. It says: make the radio look like a network interface and let IP do the rest.

A is cheap, useful immediately, and limited to streams. B is a bigger piece of work, narrower in who wants it, and the only one that gives you `ping`, UDP, or a remote site's whole LAN.

Tracked as #44 (Path A) and #45 (Path B). Path A is built: see `axsocks(1)` and `axinetd(1)`.

## Path A: the stream proxy

### Why it is the natural shape

A connected-mode AX.25 session is reliable, ordered, and flow-controlled. So is TCP. The mapping is not an analogy, it is an isomorphism, and the only thing that ever stood in the way was that axcall mangled bytes. Since #39 it does not: `-r` is verbatim in both directions, proven with 8 kB of every byte value round-tripped through two processes.

So the first piece of Path A already exists.

### A.1 One target, today

```sh
# Send a file
cat firmware.bin | axcall -r -S radio gb7rdg

# Receive one
axcall -l -r -S -W -s M0LTE-1 radio > firmware.bin
```

Nothing to build. This works now.

### A.2 Many targets: a SOCKS5 proxy

**Built.** See `axsocks(1)`.

The limitation above is that the target is fixed on the command line. SOCKS5 removes that, and SOCKS is supported by curl, ssh, browsers, and every HTTP library in wide use.

```sh
axsocks --serial /dev/ttyUSB0:57600 -s M0LTE-7 --listen 1080
```

The proxy accepts a SOCKS5 CONNECT, reads the requested hostname, maps it to a callsign, opens an AX.25 session, and relays bytes. The mapping is a hosts file alongside the ports file:

```
# /etc/axcall/hosts
gb7rdg      GB7RDG      radio
gb7cip      GB7CIP-1    radio
lab         M0LTE-9     lab-tcp
```

Then, with no application changes at all:

```sh
curl --socks5-hostname localhost:1080 http://gb7rdg/status
ssh -o ProxyCommand='nc -X 5 -x localhost:1080 %h %p' pi@lab
```

```python
import requests
requests.get("http://gb7rdg/status",
             proxies={"http": "socks5h://localhost:1080"})
```

The `socks5h` and `-X 5 -x` forms matter: they push hostname resolution to the proxy, which is what lets a name become a callsign rather than being resolved by DNS first.

### What building A.2 turned up

Three things worth recording, because none of them were visible from the design.

**One session per pair of callsigns.** The library caches a session per (local, remote) callsign pair and hands the same one back for a second connect to the same peer. Two proxied connections to one station would therefore share a link and interleave their bytes. `axsocks` refuses the second rather than corrupting both, which makes the "do not point a browser at this" warning below a hard limit rather than advice. The way out, if it is ever wanted, is the multi-callsign origination the node already uses: a pool of local SSIDs would give each conversation its own session key.

**The banner race is real.** A station that greets the caller can have bytes on the way before the dial has returned to the caller, so subscribing to a session after connecting can drop the first frame. The subscription is armed before the dial instead, keyed on the callsign rather than on the session object, which is also what stops a reused session handing a new conversation the leftovers of the last one.

**The flush-before-hangup rule transfers unchanged.** Closing the client end of a proxied connection means "I have said everything", not "drop it now", exactly as end of input does for the terminal. Without the drain, a 4 kB transfer arrives as 1 kB: one window, and the rest discarded at the disconnect. That is the same bug the terminal had once, so the drain now lives in one place and both callers use it.

### A.3 Inbound: an inetd for AX.25

**Built.** See `axinetd(1)`.

The mirror image, and the thing `ax25d` used to be. An inbound AX.25 connect either runs a program with the session on its stdin and stdout, or forwards to a local TCP port.

```
# /etc/axcall/inetd
# callsign     port     action
M0LTE-1        radio    exec   /usr/local/bin/bbs --user %r
M0LTE-2        radio    tcp    127.0.0.1:8080
M0LTE-9        -        exec   /usr/local/bin/uptime-report
```

`%r` being the calling station, so the program knows who it is talking to. With `exec`, writing a packet service becomes writing a program that reads stdin and writes stdout, in any language:

```python
#!/usr/bin/env python3
import sys
print("Hello from a three line BBS.\r", end="", flush=True)
for line in sys.stdin:
    print(f"You said: {line.strip()}\r", end="", flush=True)
```

That is the whole of the developer experience. No SDK, no protocol, no C#.

### What building A.3 turned up

**There is no authentication in AX.25, and an inetd is where that stops being abstract.** A callsign in a received frame is a claim, not a credential. `%r` and `AX25_CALLER` are a hint about who is calling and never proof, and the man page says so rather than leaving someone to assume otherwise. The same reasoning is why `axinetd` refuses to run as root: it runs programs chosen by a config file whenever a stranger calls in, and it cannot drop privileges before doing so, so one careless line would be a root shell for anyone with a radio. `--allow-root` exists for people who have thought about it.

**A program's standard error belongs to the operator, not to the caller.** Sending it down the link would leak paths and stack traces to a stranger and interleave them with whatever the program meant to say, so it goes to the log.

**A call must not be able to leave a process behind.** When the link closes, the program's stdin is closed, and anything still running ten seconds later is killed. Without that, a program that ignores EOF accumulates one process per call until the machine gives up.

**The exec form needs an absolute path.** A bare name would be resolved against whatever `PATH` the service inherited, which is not a thing to leave to chance when the trigger is a remote caller.

### What Path A costs and what it cannot do

Cheap: no root, no kernel device, no new wire protocol. It reuses the ports file, the transport resolution and the relay that axcall already has. `axsocks` and `axinetd` are each a few hundred lines on top of `SessionRelay`.

It cannot do UDP, ICMP or ping, because there is nothing to map them onto. It is streams only, by construction.

It is also worth being honest that every SOCKS connection costs a SABM/UA round trip and holds a session open. On a shared half-duplex channel you do not want a browser opening six of them. This suits one long-lived connection to a node far better than it suits chatty modern HTTP.

## Path B: the TUN device

### Shape

`axtun` creates a TUN interface, and carries IP packets over AX.25 with PID 0xCC, which `Ax25Pid.Ip` already names.

```sh
sudo axtun --serial /dev/ttyUSB0:57600 -s M0LTE-7 --dev ax0
sudo ip addr add 44.131.20.1/24 dev ax0
sudo ip link set ax0 up mtu 236
```

Then everything works, for a given value of works:

```sh
ping 44.131.20.2
ssh pi@44.131.20.2
curl http://44.131.20.2:8080/
```

And a remote site's whole network becomes reachable, which is the thing Path A can never do:

```sh
sudo ip route add 192.168.7.0/24 via 44.131.20.2 dev ax0
```

### The design calls I would argue for

**TUN, not TAP.** AX.25 is not Ethernet. TAP would force you to invent ARP-over-AX.25 and carry 14 bytes of Ethernet header on a channel whose paclen is 256. The kernel stack did this at layer 3 and so should we.

**Resolve outbound from a static map, but answer ARP.** On a slow shared channel a config file beats a discovery protocol for deciding who to send to:

```
# /etc/axcall/axtun
44.131.20.1     M0LTE-7     # us
44.131.20.2     GB7RDG      # the node
44.131.20.0/24  broadcast   QST-0
```

But refusing to speak ARP at all (PID 0xCD) is not a simplification, it is an interop failure: a station with only a static map cannot be *discovered* by anyone who does not already know it, and peers do issue ARP requests. Answering ARP for our own address while resolving outbound from the map keeps the determinism and costs almost nothing.

**Send datagram, accept both.** Running IP over a reliable ARQ link gives you two retransmit timers fighting each other, and AX.25's variable multi-second RTT wrecks TCP's RTO estimator, producing spurious retransmits that make congestion worse. So transmit IP in UI frames and let TCP own reliability, which is what it is for.

Receiving is a different question, and answering it the same way would be a bug. Both encapsulations are in use, and the kernel accepted either regardless of what a route was configured to send, per `ax25rtd.conf(5)`:

> the kernel AX.25 sends a received IP frame to the IP layer regardless if it was sent in UI frame encapsulation "mode datagram (dg)" or in I frame encaps, hence in an AX.25 connection, "mode virtual connect (vc)"

A peer configured for virtual circuit will open a session and send I-frames with PID 0xCC expecting them to work. Accept them.

**Set the MTU honestly.** 256 paclen minus overhead is about 236. Path MTU discovery will not save you; set it and move on.

**Be honest about bitrate.** A full packet at 1200 baud is about 1.7 seconds on air. This is a curiosity below 9600 and only becomes a tool at qpsk3600 and above.

**Use 44net.** AMPRNet is a live ecosystem with real allocations and real routing. A TUN device that puts a 44.x address on a radio link joins something that exists. One that invents a private range is a demo.

**Digipeat UI frames.** We closed axcall's digipeater support as not planned (#35), on the grounds that layer-2 digipeating has no place in a modern connected-mode network. That reasoning does not carry over here. Digipeating a UI frame is addressing, not the session-path problem #35 was about, and without it nothing behind a digi is reachable, which rules out a lot of real AMPRNet paths. The two decisions are allowed to differ and should.

### Interoperability is a goal, not a property

The encoding is the easy part and it interoperates: IP in AX.25 with PID 0xCC is universal, and `Ax25Pid.Ip` already names it. Everything that makes this actually talk to the installed base is behaviour, and has to be chosen deliberately:

| | Needed for | Without it |
|---|---|---|
| Accept VC as well as datagram | kernel routes set to `mode vc`, JNOS, BPQ | a VC-configured peer simply cannot reach us |
| Answer ARP (PID 0xCD) | any peer without us in its static map | we are unreachable until someone hand-configures us |
| Digipeated UI | anything behind a digi | large parts of AMPRNet are unreachable |
| Van Jacobson header compression (PID 0x06) | JNOS and NOS-derived stacks | a compressed peer is unintelligible, and we waste the channel |

The last one deserves more than a table row. `Ax25Pid` already names `CompressedTcpIp = 0x06` and `UncompressedTcpIp = 0x07`. VJ takes about 40 bytes of TCP/IP header down to about 5. Against a 236-byte MTU that is not a micro-optimisation, it is a meaningful fraction of every packet on a channel where a full frame already takes over a second. Any implementation that skips it is both slower and deaf to peers that use it.

None of this should be settled by reading man pages, which is all the above is. LinBPQ has an IP stack and is already running in this repo's Testcontainers harness for the connected-mode integration tests, so an IP-over-AX.25 interop test can be built against a real implementation. That is the first thing to do if Path B is ever picked up, before writing the TUN plumbing: prove the encapsulation against something real, then build out from there.

### What Path B costs

`CAP_NET_ADMIN`, or a persistent device created by root. Fine for a site gateway, friction for a developer tool, which is itself evidence that this serves a different audience.

It is also a genuinely bigger build: a TUN device, an IP-to-callsign layer, a broadcast and multicast policy, MTU handling, and a decision about what to do with the traffic an idle Linux box emits without being asked. That last one is not a footnote. Put an unfiltered interface on a shared radio channel and mDNS, IPv6 router solicitations, NTP and whatever else will start transmitting on a band where you are legally responsible for every emission. **Any serious axtun needs a default-deny egress filter**, not as a feature but as a condition of being shippable.

## Side by side

| | Path A, proxy | Path B, TUN |
|---|---|---|
| Serves | people writing packet apps | people running existing IP software |
| Gives you | TCP streams, any language | IP: TCP, UDP, ICMP, routing |
| Root needed | no | yes, CAP_NET_ADMIN |
| New wire protocol | none | none, IP in UI frames |
| Effort | small, reuses SessionRelay | substantial |
| Usable at 1200 baud | yes | not really |
| Risk of unintended transmission | none | high, needs egress filtering |
| Prior art | ax25d, AGW | kernel ax25_ip, AMPRNet |
| Interop burden | none, it is a socket | four separate behaviours, see above |

## What I would do

Path A, in the order A.2 then A.3, and treat Path B as a separate project judged on its own merits rather than as the next step.

The reason is that Path A converts work already done into reach. The byte-transparency fix means the hard part is proven; `axsocks` is mostly plumbing, and `axinetd` turns "write a packet service" into "write a program that reads stdin", which is the lowest barrier we can offer anyone.

Path B is the more interesting engineering and the smaller audience. It is worth doing if the goal is to administer remote sites over radio, or to connect to AMPRNet. It is not the answer to "how do I write something for the packet network", and it would be a mistake to build it in the belief that it is.

It is also more work than the first draft of this document implied. That draft treated interoperability as something the design would get for free from using the right PID, which is wrong: VC receive, ARP, digipeated UI and VJ compression are each a deliberate piece of work, and a Path B without them talks only to itself.
