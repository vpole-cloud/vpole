# UTC scheduling primitives

`Pole.Scheduling` owns the pure five-field UTC cron parser and calendar calculations.
It has no project or package dependencies and performs no I/O or scheduling of work.
Functions uses it to materialize timer occurrences. Backup providers translate its
canonical expression into their operator format; Kubernetes and provider rules stay
with those providers.

Day-of-month and day-of-week use OR when both fields are restricted. Complete day
ranges are unrestricted and Sunday accepts both 0 and 7. `ToCanonicalExpression`
expands restricted fields and normalizes Sunday to 0, preserving semantics for
operator parsers that distinguish syntactic wildcards.

`Scheduling.Tests` covers parsing, calendar boundaries, UTC and day matching.
PostgreSQL tests separately cover translation to CNPG's seconds-prefixed syntax.
