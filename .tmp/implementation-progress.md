# Naming Refactor Progress

## Scope
- Core/Domain/Events physical rename to WebServices.Application/WebServices.Domain/WebServices.Events style across all features.
- Project namespace/assembly normalization.
- Event names and EntityName update.
- Endpoint file + endpoint mapping class renames.
- Consumer names aligned to reaction semantics.
- No cross-feature ownership/move of code.
- No backwards-compatibility migration steps considered.

## Status
- [x] Branch created: `codex/naming-refactor-implementation`
- [x] Plan moved: `.tmp/naming-refactor-plan.md`
- [x] Project folder/csproj renames
- [x] Project references / .slnx updates
- [x] AssemblyName / RootNamespace updates
- [x] DnsRecords namespace normalization
- [x] Event and change record renames
- [x] Consumer renames
- [x] Endpoint file/class renames
- [x] References cleanup in API/tests
- [x] Update assembly/event names in outbox consumers/integration wiring
- [x] Build: `dotnet build Pole.slnx`
- [x] Tests: `dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj`
