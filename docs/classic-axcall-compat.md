# Compatibility with kernel AX.25 `axcall`

The Linux kernel AX.25 stack has been withdrawn, and with it the `axcall` from ax25-apps that ran on top of it. This axcall is a different program (it carries its own packet engine and speaks KISS over a serial port or TCP, where the kernel one opened an `AF_AX25` socket) but there is no reason for it to look different from the outside, so its command line is deliberately the classic one.

Compared against ax25-apps `call.c`, the binary installed as `axcall`, package version 0.0.8-rc5+git20230513, and `axcall(1)` dated 27 August 1996.

## What the command line looks like

```
axcall [options] <port> <destination>
axcall [options] --listen <port>
```

The classic short letters keep their classic meanings. Everything this axcall adds is spelled long, so it cannot collide with them.

`<port>` is one of three things, told apart by shape before anything is opened:

```
axcall radio gb7rdg                 # a name from the ports file
axcall /dev/ttyUSB0 gb7rdg          # a serial device, default baud
axcall /dev/ttyUSB0:57600 gb7rdg    # a serial device at a given baud
axcall 10.45.0.66:8001 gb7rdg       # KISS over TCP
```

A leading `/`, `./` or `../` makes it a device; a colon anywhere else makes it a host and port; anything else is a name for the ports file to resolve. `--serial` and `--tcp` give the same two transports explicitly, and when either is used the single positional is the destination.

## Flag by flag

| Classic | Meaning | Here |
|---|---|---|
| `-s mycall` | source callsign | same, and now optional when the port names one |
| `-p paclen` | N1, 1..500 | same, 1..1024 |
| `-w window` | k, 1..7 or 1..63 | same, 1..7 or 1..127 |
| `-m s\|e` | modulus 8 / 128 | same (`--mod128` is a long spelling of `-m e`) |
| `-b l\|e` | backoff | accepted and ignored, value still validated |
| `-r` | raw mode | same: a byte pipe, no translation either way |
| `-t` | talk mode | line mode, the default; the opposite of `-r` |
| `-R` | disable remote commands | accepted and ignored: axcall has none |
| `-8` | UTF-8 | accepted and ignored: axcall is always UTF-8 |
| `-i` | IBM850 | refused: ignoring it would produce mojibake, not nothing |
| `-v` | version | same (`-V` and `--version` also work) |
| `-h` | slave mode | **means `--help`**, see below |
| `-d` | `SO_DEBUG` | one line per frame, both directions |
| `-T timeout` | idle timeout | same, and hangs up properly rather than dropping the socket |
| `-W` | wait for remote disconnect | same |
| `-S` | be silent | same |
| `port callsign` | port name then destination | same |
| `[via] digi...` | digipeater path | **refused**, see below |

The four flags that ignore their argument still validate it, so `-b 9600` fails rather than passing quietly. That letter meant a baud rate in axcall 0.2.x, and this is the one place an old invocation of *this* program could otherwise have been misread.

Long options, all of them additions with no classic counterpart: `--serial`, `--tcp`, `--baud`, `--listen` (`-l`), `--mod128`, `--keepalive`, `--retries`, `--frack`, `--ack-delay`, `--no-xid`, `--paclen`, `--window`, `--mycall`, `--help`, `--version`.

Note that `--keepalive` is T3, the poll that *keeps an idle link up*, and is not classic's `-T`, which tears one down. The two are easy to confuse and both the help text and the man page say so.

`-T`, `-W`, `-S` and `-d` also have the long spellings `--idle-timeout`, `--wait`, `--silent` and `--debug`.

## The ports file

In classic, the first positional was a port *name*, and `ax25_config_load_ports()` resolved it through `/etc/ax25/axports` to a callsign, a paclen and a window; `kissattach` had separately bound a tty to that name. We cannot reuse axports itself, because it names no device: that half of the arrangement went with the kernel. So there is a file of our own that carries both halves.

```
# name   callsign   transport            paclen  window  description
radio    M0LTE-7    /dev/ttyUSB0:57600   256     4       144.800 MHz
node     M0LTE-7    10.45.0.66:8001      -       -       LinBPQ
```

Read from `/etc/axcall/ports` and then `~/.config/axcall/ports`, the second replacing same-named entries from the first, or from the single file named by `AXCALL_PORTS` when that is set. The first three columns are mandatory; `-` means "not set" in any of the optional ones, and anything past the window column is a description that is read by people and ignored by axcall. A malformed line fails the whole load rather than being skipped, because a typo in a config file should be reported and not quietly turned into "unknown port".

Precedence throughout is the obvious one: a command-line flag beats the ports file, which beats the library default. `--baud` also overrides a `:baud` suffix, and is ignored rather than refused for a TCP port, so a wrapper script that always passes it works against either kind.

This is what restores `axcall radio gb7rdg` with nothing else on the line, which is what most scripts and most muscle memory actually contain.

## Installing

The release builds a `.deb` for amd64, arm64 and armhf alongside the loose binaries, carrying the same self-contained build plus `axcall(1)`. The binary sits in `/usr/lib/axcall/` with `/usr/bin/axcall` a symlink to it, because a NativeAOT build cannot static-link the native helper that `System.IO.Ports` needs; that library is installed beside the binary, where the runtime looks for it. Without it no serial port opens at all. It declares `Conflicts: ax25-apps` and `Replaces: ax25-apps`, because that package owns `/usr/bin/axcall` and `/usr/share/man/man1/axcall.1.gz`. Installing this one therefore removes it, which also takes `axlisten`, `ax25ipd`, `ax25rtd` and `ax25mond` with it; all four are kernel-stack tools that no longer function, so in practice it removes dead weight, but it is worth knowing before you install on a machine you have not finished migrating.

No `/etc/axcall/ports` is shipped. It would be a dpkg conffile and would prompt on every upgrade, so the example goes to `/usr/share/doc/axcall/examples/ports` and you copy it. A missing ports file is not an error: axcall reads it as "no ports configured", and a device path or `host:port` works without one.

`scripts/build-deb.sh <rid> <version>` builds one package locally, and `scripts/deb-install-smoke.sh <deb>` proves it installs, runs and purges on a pristine Debian and Ubuntu in throwaway containers. Both run in the release workflow.

## One thing classic did not have

The four KISS channel-access parameters, `--txdelay`, `--persist`, `--slottime` and `--txtail`, have no counterpart in kernel `axcall`, which set exactly four socket options (`AX25_EXTSEQ`, `AX25_WINDOW`, `AX25_PACLEN`, `AX25_BACKOFF`) and never mentions persistence anywhere in `call.c`.

That was not an omission, it was the architecture. Those four are per-connection link parameters, so a connection program owned them. Channel access is per-interface, and belonged to [`kissparms(8)`](https://linux.die.net/man/8/kissparms) from ax25-tools, which set it once on the port for every program using it.

The separation is gone. There is no `kissattach` binding the port and no `kissparms` configuring it; axcall holds the only handle on the TNC, so if axcall does not set these, nothing does. They take milliseconds as kissparms did, and nothing is sent unless asked for, so a TNC set up deliberately is left alone. The ports file is usually the better home for them.

## Deliberate deviations

**`-h` means `--help`.** In classic it selects slave mode, one of three curses screen modes that do not exist here. `-h` for help is near-universal, so it wins. But `-h` alone means help, and `-h` alongside other arguments is a usage error:

```
$ axcall gb7rdg -s M0LTE --tcp 127.0.0.1:8001 -h
axcall: -h here means --help, not classic axcall's slave mode; axcall has no screen modes
$ echo $?
2
```

That case is much more likely to be a ported script asking for slave mode, and printing help and exiting 0 would look to the script like a successful call. Every other difference between the two programs fails loudly; this one had to be made to.

**Exit codes are useful.** Classic's are not: `main` runs `while (cmd_call(...))`, `cmd_call` returns FALSE when `connect_to` fails, so **a failed connect exits 0**, and only a usage error exits non-zero. Here: 0 success, 1 fatal, 2 usage, 3 could not open the modem, 4 connect refused or timed out, 5 the `-T` idle timeout fired. Nothing regresses, because a script that tested `$?` against classic never saw a failure anyway, but a script that silently tolerated a dead link will now start reporting one.

**No digipeater paths.** Classic takes up to eight after the destination, with or without the literal `via`. axcall dials direct only and refuses a path rather than ignoring it, because silently dialling direct when you asked to go via a digipeater is worse than failing. Layer-2 digipeating has no place in a modern connected-mode network: it multiplies channel occupancy on a shared half-duplex medium, gives the data link no way to tell a lost hop from a lost frame, and the routing job it was doing is better done at layer 3 or 4 by a node.

**No NET/ROM or Rose.** Classic picks the address family by trying the port name against `axports`, then `nrports`, then `rsports`, and the same binary is installed as `netromcall` and `rosecall`. We are AX.25 only.

**No file transfer, no `~` escapes, no menus.** Classic has YAPP, YAPP-C, 7plus and autobin, a status line and a menu bar. This is a pipe with a link layer under it.

With `-r` it is literally a pipe: byte transparent in both directions, which is what classic's raw mode was and what its man page meant by "-r together with -S in order to be really transparent". On end of input axcall drains whatever is still queued or unacknowledged before hanging up, so `cat file | axcall -r radio gb7rdg` delivers the whole file rather than the first window; classic got that for free from the kernel socket, and we have to do it ourselves.

## Footnote

Do not copy classic's `case 'R':` into `case 'S':` fallthrough in `call.c`, which makes `-R` silently imply `-S`. It looks like a missing `break` rather than a decision.
