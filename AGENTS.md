# Repository guidance

This file applies to the entire repository. Read the relevant project README and
nearby implementation and tests before making changes.

## Skills

The general conventions for this repository live in agent skills under
`.agents/skills/` and `.claude/skills/`. Use the skill that matches the task, and
follow it where it differs from older code or other guidance.

| Task | Skill |
| --- | --- |
| Writing or editing C#, `.csproj`, `.props`, `.targets`, or `Directory.Packages.props` | `brows-csharp` |
| Restoring, building, and choosing the validation scope | `brows-code-building` |
| Writing and running NUnit tests | `brows-code-testing` |
| Git status, staging, commits, branches, and worktrees | `brows-git` |
| Planning substantial changes under `agents/plans/` | `brows-code-plan` |
| Reviewing code and writing reports under `agents/reviews/` | `brows-code-review` |
| Fixing numbered review issues, including per-issue worktrees | `brows-code-fix` |
| Performance and SIMD analysis | `analyzing-dotnet-performance`, `vectorization` |

## Solution layout

- `brows-operations.slnx` is the solution entry point.
- `source/Brows.Operations` contains the operation interfaces, execution,
  hierarchical progress, cancellation, notifications, and observable collections.
- `source/Brows.Operations.Composition` exports `IOperatorFactory` through
  Brows.Composition and creates the core operator implementation.
- `source/Brows.Operations.Windows` contains the WPF `OperatorControl`, command
  converter, templates, and styles.
- `tests/` contains test projects corresponding to the source projects,
  synchronization helpers, and compatibility shims.
- `samples/Brows.Operations.Sample` is a WPF application demonstrating the API.

## Repository specifics

- `global.json` pins the .NET SDK. The solution contains only SDK-style managed
  projects, so build and test it with the .NET CLI from the repository root.
  The WPF projects and the sample require Windows.
- `Directory.Build.props` sets the shared target frameworks
  (`net462;net48;net8.0;net10.0`; Windows projects use the `-windows` variants),
  `LangVersion` 14.0, the `Brows` root namespace, and global type aliases. Build
  output goes to `out/`.
- Because the projects target .NET Framework and .NET 8 as well as .NET 10, guard
  newer runtime APIs such as `Lock` with `NET9_0_OR_GREATER` and keep the existing
  compatibility shims.
- The CI configuration is in `.github/workflows/workflow.yml`.
