# Acceptance test: exercising all four tools over the air

A guide for one person, two radios and an afternoon. It walks `axcall`, `axsocks`, `axinetd` and `axtun` from a wiped machine to a working IP link, in an order where each phase proves the one before it was real.

Not exhaustive. It is built round the things that have actually broken, or that nothing else checks.

## What it assumes

Two stations, each with a radio and a `pdn-soundmodem` exposing a KISS TCP port, working over the air at low power. Call them **radio1** and **radio2**. Anything that speaks KISS will do; the point is that both ends are real and the path is RF.

Substitute throughout:

| | |
|---|---|
| `CALL1` / `CALL2` | your callsigns for radio1 and radio2 |
| `KISS1` / `KISS2` | each machine's local KISS TCP port, e.g. `127.0.0.1:8110` |

**One gap to know about up front.** Reaching the modem over TCP never touches the serial code path, and serial is where two consecutive releases shipped broken. Phase 7 covers it if you have a TNC on a serial port; if you do not, that path stays untested and it is worth saying so in the result.

## How to use it

Each step says what to run, what you should see, and what it proves. Work down. If something does not match, stop there rather than pressing on: later phases assume the earlier ones worked.

There is a results table at the end.

---

# Phase 0 : install

On **both** machines.

**0.1** Which package:

```sh
uname -m          # aarch64 -> arm64,  armv7l -> armhf
```

**0.2** Fetch the four packages for that architecture from the [latest release](https://github.com/packet-net/axcall/releases/latest) and install them together:

```sh
sudo apt install ./axcall_*.deb ./axsocks_*.deb ./axinetd_*.deb ./axtun_*.deb
```

*Proves:* the declared dependencies resolve on a stock OS. There is no .NET to install; the runtime is inside each binary.

**0.3** They run:

```sh
for t in axcall axsocks axinetd axtun; do $t --version; done
```

*Proves:* no missing native dependency. A self-contained build that is short one library installs perfectly and then fails on first exec, so running it is the only check that counts.

**0.4** Documentation landed:

```sh
man axcall | head -5
ls /usr/share/doc/axtun/examples/
```

**0.5** Write `/etc/axcall/ports` on each machine. On radio1:

```
# name   callsign   device   paclen  window
radio    CALL1      KISS1    256     4
```

and the same on radio2 with `CALL2` and `KISS2`.

**0.6** Check it parsed:

```sh
axcall notaport CALL2
```

Expect: `unknown port 'notaport' in /etc/axcall/ports; known ports: radio`

*Proves:* the file was found and read. If it says something else, fix that before going on.

---

# Phase 1 : the link exists

The simplest possible proof, and the foundation for everything after it. Two terminals.

**1.1** On **radio2**, listen:

```sh
axcall -l radio
```

**1.2** On **radio1**, call it:

```sh
axcall radio CALL2
```

**1.3** Type a line on each and press Enter. It should appear on the other.

**1.4** Press **Ctrl-D** on radio1.

Expect: radio1 reports the link closing; radio2 returns to a prompt.

*Proves:* connected-mode AX.25 end to end over RF, in both directions, with a clean teardown. If this does not work, nothing else will.

**1.5** Now repeat 1.1 and 1.2 with `-d` on both ends, and watch the frames:

```sh
axcall -d radio CALL2
```

Expect something like `> CALL1>CALL2 SABM C P` then `< CALL2>CALL1 UA R F`, then `I` frames as you type, and `RR` acknowledgements coming back.

*Proves:* the link is doing real AX.25, and gives you the tool for diagnosing everything below.

---

# Phase 2 : axcall in anger

**2.1 Byte transparency.** This is the one that was once broken, and it is load-bearing for every other tool.

Make a binary file on radio1 that is comfortably bigger than one window:

```sh
head -c 4096 /dev/urandom > /tmp/send.bin
sha256sum /tmp/send.bin
```

On **radio2**:

```sh
axcall -l -r radio > /tmp/recv.bin
```

On **radio1**:

```sh
axcall -r radio CALL2 < /tmp/send.bin
```

When radio1 exits, Ctrl-C radio2 and compare:

```sh
sha256sum /tmp/recv.bin
```

Expect: identical hashes, and **4096 bytes**, not 1024.

*Proves:* raw mode is a clean byte pipe, and the drain before hang-up works. Without the drain this arrives truncated at one window, which is exactly how it used to fail.

**2.2 Link parameters.** Dial with a smaller packet length and a wider window:

```sh
axcall -p 128 -w 7 -d radio CALL2
```

*Proves:* N1 and k reach the link. Watch the status line and the frame trace; the I-frames should be no larger than 128 bytes of payload.

**2.3 Keep the link up after input ends:**

```sh
echo hello | axcall -W radio CALL2
```

Expect: the link stays open and keeps printing until you interrupt it.

*Proves:* `-W`, which is what you want when the far end answers slowly.

---

# Phase 3 : axinetd

Now make radio2 answer for itself, rather than you sitting at a terminal.

**3.1** On **radio2**, a service worth calling:

```sh
sudo tee /usr/local/bin/greet >/dev/null <<'EOF'
#!/usr/bin/env python3
import os, sys
print(f"Hello {os.environ.get('AX25_CALLER','?')}, this is radio2.\r", end="", flush=True)
for line in sys.stdin:
    print(f"You said: {line.strip()}\r", end="", flush=True)
EOF
sudo chmod +x /usr/local/bin/greet
```

**3.2** `/etc/axcall/inetd` on radio2:

```
# callsign   port    action
CALL2-1      radio   exec  /usr/local/bin/greet --caller %r
```

**3.3** Run it **as an ordinary user**, not root:

```sh
axinetd radio
```

**3.4** From **radio1**:

```sh
axcall radio CALL2-1
```

Expect: `Hello CALL1, this is radio2.` Then type lines and see them echoed back.

*Proves:* inbound calls are answered, the callsign selects the service, and the caller's identity reaches the program in both `%r` and `AX25_CALLER`. Writing a packet service is now writing a program that reads stdin.

**3.5 Standard error stays local.** Add `import sys; print("diagnostic", file=sys.stderr)` to the script, reconnect, and confirm the word `diagnostic` appears in axinetd's terminal on radio2 and **not** in the session on radio1.

*Proves:* a program's noise goes to the operator, not down the link to a stranger.

**3.6 One call at a time.** With a session open from radio1, try a second `axcall radio CALL2-1` from the same machine.

Expect: the second is not given a second copy of the program.

---

# Phase 4 : axsocks

Reach that same service from a socket, with nothing knowing what AX.25 is.

**4.1** On **radio1**, `/etc/axcall/hosts`:

```
# name    callsign   port
radio2    CALL2-1    radio
```

**4.2** Start the proxy (leave axinetd running on radio2):

```sh
axsocks radio
```

Expect a line saying it is listening on `127.0.0.1:1080`.

**4.3** In another terminal on radio1:

```sh
nc -X 5 -x 127.0.0.1:1080 radio2 1
```

Type a line. Expect the same greeting and echo as Phase 3, through a plain socket.

*Proves:* the whole point of `axsocks`. Anything that speaks SOCKS5 now reaches a packet station.

**4.4 Names are not required.** Kill the `nc` and use the callsign directly:

```sh
nc -X 5 -x 127.0.0.1:1080 CALL2-1 1
```

*Proves:* a name that is already a callsign needs no hosts entry.

**4.5 Refuse what it cannot do.** Ask for an IP address rather than a name:

```sh
nc -X 5 -x 127.0.0.1:1080 44.131.20.2 1
```

Expect: a refusal naming `--socks5-hostname`.

*Proves:* it fails with an explanation rather than silently doing the wrong thing. This is the mistake every SOCKS client makes.

---

# Phase 5 : axtun

The big one. IP over the air, both directions.

**5.1** On **radio1**:

```sh
sudo axtun --dev ax0 --addr 44.131.20.1/24 -s CALL1 radio
```

On **radio2**:

```sh
sudo axtun --dev ax0 --addr 44.131.20.2/24 -s CALL2 radio
```

Expect on each: a line naming the interface and address, the egress policy, and `0 routes configured; every destination will be ARPed for`.

**5.2 Watch the filter work.** Within a few seconds of the interface coming up, both should print drops like:

```
axtun: dropped udp 44.131.20.1->224.0.0.252 5355->5355 54 bytes: no rule
names this multicast or broadcast range, and a wildcard rule does not cover one
```

*Proves:* the machine tried to announce itself to the neighbourhood over your licence within seconds, and was stopped. This is the single most important thing `axtun` does.

**5.3 Ping, with nothing configured:**

```sh
ping -c 5 44.131.20.2
```

Expect: the **first packet lost**, the rest succeeding.

*Proves:* ARP discovery. The first packet is dropped while the request goes out, and whatever sent it retries. Both stations found each other with no route table at all. Watch for `who has ...? asked QST` and `told CALL2 that ... is CALL1` in the logs.

**5.4 A real TCP session:**

```sh
ssh CALL2@44.131.20.2        # or: curl http://44.131.20.2/ if you run a web server
```

*Proves:* the thing this tool exists for. Software that has never heard of AX.25, working over radio.

**5.5 Measure it.** Copy a small file and time it:

```sh
time scp /tmp/send.bin 44.131.20.2:/tmp/
```

Write the number down. At 1200 baud expect it to be painful; the tool becomes useful at 9600 and above, and this is where you find out what your link really does.

**5.6 Configured routes.** Stop both, put this in `/etc/axcall/axtun` on radio1:

```
route 44.131.20.2   CALL2
allow icmp
```

Restart and ping again. Expect: **no lost first packet**, because nothing has to be discovered. And expect `ssh` to now be refused by the filter, with a line saying so.

*Proves:* the route table and the filter, both doing exactly what they say.

---

# Phase 6 : the refusals

Short, and each one is a thing that should fail rather than surprise you later.

**6.1** `axinetd` will not run as root:

```sh
sudo axinetd radio
```

Expect: `refusing to run as root: axinetd runs programs on behalf of whoever calls in...`

**6.2** No digipeater paths anywhere:

```sh
axcall radio CALL2 WIDE1-1
```

Expect: `digipeater paths are not supported (got 'WIDE1-1'); axcall dials direct only`

Add `route 44.131.20.2 CALL2 via WIDE1-1` to `/etc/axcall/axtun` and start `axtun`. Expect it to name the file and line and refuse.

**6.3** An MTU that cannot work:

```sh
sudo axtun --addr 44.131.20.1/24 --mtu 5000 radio
```

Expect: `invalid --mtu: 5000 (must be 68..1008 bytes)`

**6.4** An MTU that is merely unwise:

```sh
sudo axtun --dev ax0 --addr 44.131.20.1/24 --mtu 400 -s CALL1 radio
```

Expect: it starts, and warns that 400 is above the largest packet every peer tested so far accepts.

*Proves:* the difference between a limit we measured and a limit we invented. It warns rather than refusing, because the ceiling came from one implementation.

**6.5** Missing configuration is not an error:

```sh
sudo mv /etc/axcall/ports /etc/axcall/ports.bak
axcall --tcp KISS1 -s CALL1 CALL2
sudo mv /etc/axcall/ports.bak /etc/axcall/ports
```

*Proves:* no ports file means "no ports configured", not a failure. A device path or `host:port` works without one.

---

# Phase 7 : the serial path, if you can

**Only if you have a KISS TNC on a serial port.** Everything above reached the modem over TCP, which never loads the serial library. That library is shipped beside the binary rather than inside it, and getting that wrong is how v0.5.0 and v0.6.0 shipped with every serial port unopenable while passing every other check.

```sh
axcall --serial /dev/ttyUSB0:9600 -s CALL1 CALL2
```

Expect: it reaches the device. If it says `Unable to load shared library`, that is the bug back.

If you cannot do this, record the phase as **not tested** rather than passed.

---

# Teardown

```sh
sudo pkill axtun; sudo pkill axsocks; sudo pkill axinetd
sudo ip link del ax0            # on both, if it survived
sudo apt purge axcall axsocks axinetd axtun
```

Check `/usr/bin/axcall` and `/usr/lib/axtun` are gone. Nothing should be left in `/etc/axcall` that you did not put there yourself: no configuration is shipped, deliberately, so that upgrades never prompt.

---

# Results

| Phase | | Notes |
|---|---|---|
| 0 install | | |
| 1 link exists | | |
| 2 axcall, transparency | | |
| 3 axinetd | | |
| 4 axsocks | | |
| 5 axtun | | |
| 6 refusals | | |
| 7 serial | | or "not tested" |

Worth recording alongside: the modem and rate you used, the distance and power, and the `scp` figure from 5.5. The last one is the only honest answer to "is this usable", and it depends entirely on your link.
