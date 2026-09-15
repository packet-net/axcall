#!/bin/sh
# Bridge a KISS-over-TCP channel to a pty, then hand over to XRouter.
#
# $KISS_TCP is host:port; $KISS_PTY is where the device node should appear, and
# is what XROUTER.CFG's ASYNC interface points at.
#
# socat has to be up before XRouter opens the port. XRouter reports a missing
# device once at boot and does not retry, so losing that race looks like a node
# that is running but deaf, which is a miserable thing to debug.
set -e

KISS_TCP="${KISS_TCP:?set KISS_TCP to host:port of the KISS channel}"
KISS_PTY="${KISS_PTY:-/dev/kisspty}"

echo "with-kiss-pty: bridging $KISS_PTY to $KISS_TCP"
socat "pty,link=$KISS_PTY,raw,echo=0,waitslave" "tcp:$KISS_TCP,forever,intervall=1,retry=30" &
socat_pid=$!

# Wait for the device node rather than sleeping: socat creates the link after
# it has opened the pty, and on a loaded machine that is not instant.
i=0
while [ ! -e "$KISS_PTY" ]; do
    i=$((i + 1))
    if [ "$i" -gt 100 ]; then
        echo "with-kiss-pty: $KISS_PTY never appeared" >&2
        exit 1
    fi
    if ! kill -0 "$socat_pid" 2>/dev/null; then
        echo "with-kiss-pty: socat exited before creating $KISS_PTY" >&2
        exit 1
    fi
    sleep 0.1
done
echo "with-kiss-pty: $KISS_PTY is up"

exec /usr/local/bin/xrouter-entrypoint.sh "$@"
