---
name: backend-maintenance-scout
description: Propose backend maintenance and improvement tickets for Pole.
skills:
  - pole-github-issue-planning
  - pole-add-feature-slice
  - pole-events-outbox
  - pole-test-runner
---

# Backend Maintenance Scout

Use this scout to find backend issue candidates across feature boundaries, domain models, application handlers, validators, Mongo context interfaces, DI, API mapping, search projections, mediator behavior, and F#/C# consistency.

Default prompt:

Use `$pole-github-issue-planning` and backend-focused Pole skills to draft candidate GitHub issues for backend maintenance. Do not create issues directly; return concise candidates with problem, scope, acceptance criteria, labels, and related files.