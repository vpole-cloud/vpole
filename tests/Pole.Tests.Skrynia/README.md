# Local Skrynia package integration

This standalone test project exercises the production `Pole.Database.MongoRunTransaction` against a packaged Skrynia provider. Only the fixture opens/closes Skrynia; test callbacks use Pole and MongoDB.Driver types. The tests open their own isolated files; MongoDB remains Pole.Server's default provider.

The focused consumer project stays outside `Pole.slnx` and is built and run explicitly. Place the Skrynia worktree next to the Pole worktree as `skrynia-driver-compat`, and pack into its `artifacts/packages` directory. `NuGet.Config` maps only `Skrynia` to that feed; it does not change user-wide NuGet configuration.

From this Pole worktree, using the package version in the root `Directory.Packages.props`:

```bash
dotnet restore tests/Pole.Tests.Skrynia/Pole.Tests.Skrynia.csproj --configfile tests/Pole.Tests.Skrynia/NuGet.Config --disable-build-servers --nologo -maxcpucount:1 -nodeReuse:false
dotnet build tests/Pole.Tests.Skrynia/Pole.Tests.Skrynia.csproj --no-restore --disable-build-servers --nologo -maxcpucount:1 -nodeReuse:false
dotnet test tests/Pole.Tests.Skrynia/Pole.Tests.Skrynia.csproj --no-build --no-restore --nologo
```

To test another package, pass `-p:SkryniaPackageVersion=<version>` to restore and build. The package must exist in the local feed. Tests require supported local Linux storage (such as Btrfs/ext4), not tmpfs; no MongoDB service or container is needed.

The expanded suite also runs production migrations with their feature prerequisites, repeats them after reopening the file, and verifies the complete MigrationRunner lock/history lifecycle. Pole.Server now has an experimental provider switch; see `docs/skrynia-local-development.md`.
