# Real database transport validation

The infrastructure suite runs real PostgreSQL 18 and Redis 8.2 in disposable containers. Clients
connect through the loopback local forward, broker and outbound connector; they never use the
container port directly for the behavior under test. Container ports are only connector routes.
Npgsql pooling is disabled so opening a connection always exercises fresh transport admission.

## Acceptance cases

| Case | Evidence required |
| --- | --- |
| PostgreSQL transactions | Commit persists, rollback does not, binary payload exceeds one transport frame |
| Redis regular commands | Binary value survives a round trip through a real client |
| Redis Pub/Sub | RESP2/RESP3 subscription acknowledgement, delivery and disconnect after revoke |
| Redis blocking command | BLPOP succeeds after push from another connection; indefinite blocking ends on revoke |
| Sustained session | Same connection survives 35 seconds of probes plus six seconds idle, spanning many leases and the idle registration timeout |
| Concurrent sessions | Four PostgreSQL connections make progress concurrently and all lose access after revoke or expiry |
| Restart | Connector, broker and database restart close affected sessions; fresh authorized connections recover |
| Fresh admission | Revoked/expired grants cannot open another database connection |

These are bounded correctness checks, not a throughput benchmark or an hours-long soak test.
Transport revocation closes the TCP connection; it does not promise cancellation or rollback of
work the database already accepted. Database timeouts and cancellation policies remain separate.
The database leg uses plaintext in this fixture; database-native TLS is not established by these tests.

## Reproduction

Start the Docker-compatible runtime and the repository lab as described in
[testing](../../../docs/testing.md), then build the solution and run:

```bash
dotnet test src/examples/PrivateAccess/PrivateAccess.Tests.Integration/PrivateAccess.Tests.Integration.csproj --no-build --no-restore
```

The suite is discovered by `scripts/quality_gate.py`; it is not opt-in and has no infrastructure
skip path. Containers have Pole test ownership labels, dynamically assigned ports and scoped
teardown. Missing prerequisites fail the suite. Tests do not modify the reusable Kubernetes lab.
