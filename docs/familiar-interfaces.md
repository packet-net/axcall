# Reaching the packet network without writing C#

Status: design notes, nothing implemented. Written 2026-09-14, after axcall 0.4.0 and the byte-transparency work in #39.

## The question

The kernel AX.25 stack gave every program on the machine a way in: `AF_AX25` sockets and `ax0` interfaces. Anything could use it, in any language, with no library. That is gone, and what replaced it is a set of .NET libraries. If you write C#, you are well served. If you do not, you are not served at all.

There are two answers to that, and they are not variations on one idea. They serve different people and they should be judged separately.

**Path A, the stream proxy**, serves people writing applications. It says: an AX.25 session is a byte stream, a TCP socket is a byte stream, so put a socket in front of it and every language is invited.

**Path B, the TUN device**, serves people who want software that already exists to work over radio. It says: make the radio look like a network interface and let IP do the rest.

A is cheap, useful immediately, and limited to streams. B is a bigger piece of work, narrower in who wants it, and the only one that gives you `ping`, UDP, or a remote site's whole LAN.

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

The limitation above is that the target is fixed on the command line. SOCKS5 removes that, and SOCKS is supported by curl, ssh, browsers, and every HTTP library in wide use.

```sh
axsocks --listen 1080 --serial /dev/ttyUSB0:57600 -s M0LTE-7
```

The proxy accepts a SOCKS5 CONNECT, reads the requested hostname, maps it to a callsign, opens an AX.25 session, and relays bytes. The mapping wants to be the ports file, extended:

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

### A.3 Inbound: an inetd for AX.25

The mirror image, and the thing `ax25d` used to be. An inbound AX.25 connect either runs a program with the session on its stdin and stdout, or forwards to a local TCP port.

```
# /etc/axcall/inetd
# callsign     port     action
M0LTE-1        radio    exec   /usr/local/bin/bbs --user %r
M0LTE-2        radio    tcp    127.0.0.1:8080
M0LTE-9        *        exec   /bin/sh -c "uptime"
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

**A static callsign-to-IP map, not ARP.** On a slow shared channel a config file beats a discovery protocol. PID 0xCD exists for AX.25 ARP; do not use it.

```
# /etc/axcall/axtun
44.131.20.1     M0LTE-7     # us
44.131.20.2     GB7RDG      # the node
44.131.20.0/24  broadcast   QST-0
```

**UI frames, not connected mode.** This is the one people get wrong. Running IP over a reliable ARQ link gives you two retransmit timers fighting each other, and AX.25's variable multi-second RTT wrecks TCP's RTO estimator, producing spurious retransmits that make congestion worse. Send IP in UI frames and let TCP own reliability, which is what it is for.

**Set the MTU honestly.** 256 paclen minus overhead is about 236. Path MTU discovery will not save you; set it and move on.

**Be honest about bitrate.** A full packet at 1200 baud is about 1.7 seconds on air. This is a curiosity below 9600 and only becomes a tool at qpsk3600 and above.

**Use 44net.** AMPRNet is a live ecosystem with real allocations and real routing. A TUN device that puts a 44.x address on a radio link joins something that exists. One that invents a private range is a demo.

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

## What I would do

Path A, in the order A.2 then A.3, and treat Path B as a separate project judged on its own merits rather than as the next step.

The reason is that Path A converts work already done into reach. The byte-transparency fix means the hard part is proven; `axsocks` is mostly plumbing, and `axinetd` turns "write a packet service" into "write a program that reads stdin", which is the lowest barrier we can offer anyone.

Path B is the more interesting engineering and the smaller audience. It is worth doing if the goal is to administer remote sites over radio, or to connect to AMPRNet. It is not the answer to "how do I write something for the packet network", and it would be a mistake to build it in the belief that it is.
