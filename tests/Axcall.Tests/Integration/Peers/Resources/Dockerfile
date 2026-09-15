# XRouter, plus the one thing it needs to reach a KISS-over-TCP channel: a
# serial port.
#
# XRouter's only KISS-capable interface type is ASYNC, which wants a device
# node. IFACES(6) lists AGW, ASYNC, AXIP, AXTCP, AXUDP, EXTERNAL, LOOPBACK,
# TCP, TUN, UDP and YAM, and EXTERNAL is Ethernet, so there is no KISS-over-TCP
# client type to use instead. net-sim speaks KISS over TCP. socat bridges them.
#
# The same bridge is what kissattach needs for the kernel AX.25 stack, for the
# same reason.
FROM ghcr.io/packethacking/xrouter:latest

RUN apt-get update \
 && apt-get install -y --no-install-recommends socat \
 && rm -rf /var/lib/apt/lists/*

COPY with-kiss-pty.sh /usr/local/bin/with-kiss-pty.sh
RUN chmod 0755 /usr/local/bin/with-kiss-pty.sh

ENTRYPOINT ["/usr/local/bin/with-kiss-pty.sh"]
