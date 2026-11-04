# Running-package update compatibility repair, 2026-11-04

Observed locally: LaunchDeck/proxy host started at 09:44; the 09:44 desktop
session used 26.1027.6604.0. At 09:46 the desktop switched to 26.1029.7803.0
without restarting the proxy host. Existing code created exact executable rules
once, before package activation, and did not follow the replacement package or
new copied core runtime. Remote controller logs subsequently showed hundreds of
WebSocket handshake timeouts; local host logs showed OS error 10060.

An unauthenticated request to the remote server endpoint via 127.0.0.1:10808
returned HTTP 426 in 1.34 seconds; direct TCP connection timed out after five
seconds. This establishes a network-path difference, not a completed authenticated
remote connection. The old live host has no current diagnostic file because its
eight retained session slots are full; its exact in-memory rule list was not read.

Repair: maintain registered package/helper scope within the protected host every
15 seconds. Preserve the existing proxy and all working rules; add newly verified
exact paths transactionally. Retry incomplete updates and failed rule additions.
Reject upstream/family changes and bound growth to 64 executable paths. No app
ASAR modification, access-grant changes, system proxy, TUN or DNS changes.

Validation: 137 managed assertions cover the new package path, late core creation,
idempotence, unchanged upstream/family, partial rollback, retry, and STOP handling
with a single pending pipe read. These tests use simulated engines and do not
prove Dot is online. After restarting with the repaired launcher, the authenticated
remote environment roster reported this local computer online. The remaining
offline error mapped to a different saved computer last seen on October 8; it was
left untouched at the user's request. No real app auto-update was forced during
acceptance, and the old host's in-memory rule list was not inspected.
