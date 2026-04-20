# ZeroAlloc.Outbox Infrastructure & Docs Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Bring ZeroAlloc.Outbox to full parity with ZeroAlloc.Mediator: CI/CD pipelines, release automation, dependency management, commit linting, GitHub templates, a complete README, and a full documentation set for the website.

**Architecture:** Three independent concerns implemented together. Infrastructure (root config + GitHub Actions) follows Mediator exactly, adapted for 4 Outbox packages. Documentation follows Mediator's docs/ structure but is adapted to the outbox domain. README mirrors ZeroAlloc.Cache style with 4-package install block, features table, and diagnostics table.

**Tech Stack:** GitHub Actions, release-please v4, GitVersion 6 (ContinuousDeployment), Renovate, commitlint, Docusaurus frontmatter, .NET 10, 4 NuGet packages (`ZeroAlloc.Outbox`, `ZeroAlloc.Outbox.Generator`, `ZeroAlloc.Outbox.EfCore`, `ZeroAlloc.Outbox.InMemory`).

---

## Task 1: SDK pin + GitVersion tool manifest

**Files:**
- Create: `global.json`
- Create: `.config/dotnet-tools.json`
- Create: `GitVersion.yml`

**Context:** These three files ensure the .NET SDK version is pinned for reproducible builds, the `dotnet-gitversion` tool is available via `dotnet tool restore`, and GitVersion knows how to derive the semantic version from the branch name.

**Step 1: Create `global.json`**

```json
{
  "sdk": {
    "version": "10.0.104",
    "rollForward": "latestMinor"
  }
}
```

**Step 2: Create `.config/dotnet-tools.json`**

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "gitversion.tool": {
      "version": "6.6.2",
      "commands": ["dotnet-gitversion"],
      "rollForward": false
    }
  }
}
```

**Step 3: Create `GitVersion.yml`**

```yaml
mode: ContinuousDeployment
tag-prefix: v
major-version-bump-message: "^(build|chore|ci|docs|feat|fix|perf|refactor|revert|style|test)(\\(.*\\))?!:"
minor-version-bump-message: "^feat(\\(.*\\))?:"
patch-version-bump-message: "^fix(\\(.*\\))?:"
branches:
  main:
    regex: ^main$
    label: alpha
  release:
    regex: ^release/.*$
    label: rc
```

**Step 4: Commit**

```bash
git add global.json .config/dotnet-tools.json GitVersion.yml
git commit -m "build: add SDK pin, GitVersion tool manifest, and GitVersion config"
```

---

## Task 2: Release and dependency tooling

**Files:**
- Create: `release-please-config.json`
- Create: `.release-please-manifest.json`
- Create: `renovate.json`
- Create: `.commitlintrc.yml`

**Context:** `release-please-config.json` + `.release-please-manifest.json` drive automated changelog and GitHub Release creation. `renovate.json` handles dependency updates on a schedule. `.commitlintrc.yml` enforces conventional commits with Outbox-specific scopes. The manifest is seeded at `0.1.0`; release-please will update it automatically after the first release.

**Step 1: Create `release-please-config.json`**

```json
{
  "$schema": "https://raw.githubusercontent.com/googleapis/release-please/main/schemas/config.json",
  "packages": {
    ".": {
      "release-type": "simple",
      "bump-minor-pre-major": true,
      "bump-patch-for-minor-pre-major": true,
      "changelog-sections": [
        { "type": "feat",     "section": "Features" },
        { "type": "fix",      "section": "Bug Fixes" },
        { "type": "perf",     "section": "Performance" },
        { "type": "refactor", "section": "Refactoring" },
        { "type": "docs",     "section": "Documentation" },
        { "type": "test",     "section": "Tests",        "hidden": true },
        { "type": "build",    "section": "Build",        "hidden": true },
        { "type": "ci",       "section": "CI",           "hidden": true },
        { "type": "chore",    "section": "Chores",       "hidden": true }
      ]
    }
  }
}
```

**Step 2: Create `.release-please-manifest.json`**

```json
{
  ".": "0.1.0"
}
```

**Step 3: Create `renovate.json`**

```json
{
  "$schema": "https://docs.renovatebot.com/renovate-schema.json",
  "extends": [
    "config:recommended"
  ],
  "schedule": ["before 6am on monday"],
  "timezone": "Europe/Amsterdam",
  "labels": ["dependencies"],
  "packageRules": [
    {
      "description": "Ignore internal ZeroAlloc packages — managed by release-please",
      "matchPackagePrefixes": ["ZeroAlloc."],
      "enabled": false
    },
    {
      "description": "Group Roslyn analyzer packages",
      "matchPackageNames": [
        "Meziantou.Analyzer",
        "Roslynator.Analyzers",
        "ErrorProne.NET.CoreAnalyzers",
        "ErrorProne.NET.Structs",
        "NetFabric.Hyperlinq.Analyzer"
      ],
      "groupName": "Roslyn analyzers"
    },
    {
      "description": "Group xunit packages",
      "matchPackagePrefixes": ["xunit"],
      "groupName": "xunit"
    },
    {
      "description": "Group Microsoft.Extensions packages",
      "matchPackagePrefixes": ["Microsoft.Extensions."],
      "groupName": "Microsoft.Extensions"
    },
    {
      "description": "Group Microsoft.CodeAnalysis packages",
      "matchPackagePrefixes": ["Microsoft.CodeAnalysis."],
      "groupName": "Microsoft.CodeAnalysis"
    },
    {
      "description": "Group GitHub Actions",
      "matchManagers": ["github-actions"],
      "groupName": "GitHub Actions"
    },
    {
      "description": "Automerge patch updates",
      "matchUpdateTypes": ["patch"],
      "automerge": true
    }
  ]
}
```

**Step 4: Create `.commitlintrc.yml`**

Scopes are adapted from Mediator (`benchmarks`/`sample` → `efcore`/`inmemory`):

```yaml
extends:
  - "@commitlint/config-conventional"

rules:
  type-enum:
    - 2
    - always
    - - feat
      - fix
      - docs
      - style
      - refactor
      - perf
      - test
      - build
      - ci
      - chore
      - revert

  scope-enum:
    - 1
    - always
    - - core
      - generator
      - efcore
      - inmemory
      - docs
      - ci
      - deps

  subject-case:
    - 2
    - never
    - - sentence-case
      - start-case
      - pascal-case
      - upper-case

  header-max-length:
    - 2
    - always
    - 100
```

**Step 5: Commit**

```bash
git add release-please-config.json .release-please-manifest.json renovate.json .commitlintrc.yml
git commit -m "ci: add release-please config, renovate, and commitlint"
```

---

## Task 3: GitHub CI workflow

**Files:**
- Create: `.github/workflows/ci.yml`

**Context:** Triggers on push to `main`/`release-please--**` and PRs to `main`. Runs GitVersion to derive `SemVer`, builds, tests, packs all 4 packages, and pushes pre-release to NuGet on `main` push only. `fetch-depth: 0` is required for GitVersion to walk the full tag history.

**Step 1: Create `.github/workflows/ci.yml`**

```yaml
name: CI

on:
  push:
    branches: [main, 'release-please--**']
  pull_request:
    branches: [main]
  workflow_dispatch:

jobs:
  build:
    runs-on: ubuntu-latest

    steps:
      - name: Checkout
        uses: actions/checkout@v6
        with:
          fetch-depth: 0

      - name: Setup .NET
        uses: actions/setup-dotnet@v5
        with:
          dotnet-version: 10.0.x

      - name: Restore tools
        run: dotnet tool restore

      - name: Run GitVersion
        id: gitversion
        run: |
          VERSION=$(dotnet gitversion /showvariable SemVer)
          echo "version=$VERSION" >> "$GITHUB_OUTPUT"
          echo "Package version: $VERSION"

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore -c Release -p:Version=${{ steps.gitversion.outputs.version }}

      - name: Test
        run: dotnet test --no-build -c Release --verbosity normal

      - name: Pack
        run: |
          dotnet pack src/ZeroAlloc.Outbox/ZeroAlloc.Outbox.csproj --no-build -c Release -p:PackageVersion=${{ steps.gitversion.outputs.version }} -o ./artifacts
          dotnet pack src/ZeroAlloc.Outbox.Generator/ZeroAlloc.Outbox.Generator.csproj --no-build -c Release -p:PackageVersion=${{ steps.gitversion.outputs.version }} -o ./artifacts
          dotnet pack src/ZeroAlloc.Outbox.EfCore/ZeroAlloc.Outbox.EfCore.csproj --no-build -c Release -p:PackageVersion=${{ steps.gitversion.outputs.version }} -o ./artifacts
          dotnet pack src/ZeroAlloc.Outbox.InMemory/ZeroAlloc.Outbox.InMemory.csproj --no-build -c Release -p:PackageVersion=${{ steps.gitversion.outputs.version }} -o ./artifacts

      - name: Push to NuGet (pre-release)
        if: github.event_name == 'push' && github.ref == 'refs/heads/main'
        run: dotnet nuget push ./artifacts/*.nupkg --api-key ${{ secrets.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json --skip-duplicate
```

**Step 2: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add CI workflow (build, test, pack, pre-release push)"
```

---

## Task 4: Release-Please and Release workflows

**Files:**
- Create: `.github/workflows/release-please.yml`
- Create: `.github/workflows/release.yml`

**Context:** `release-please.yml` has two jobs: job 1 runs the release-please action to create/update the release PR; job 2 conditionally publishes on `release_created`. `release.yml` is a fallback triggered on GitHub Release published (manual releases). Both pack all 4 packages.

**Step 1: Create `.github/workflows/release-please.yml`**

```yaml
name: Release Please

on:
  push:
    branches: [main]

permissions:
  contents: write
  pull-requests: write

jobs:
  release-please:
    runs-on: ubuntu-latest
    outputs:
      release_created: ${{ steps.release.outputs.release_created }}
      tag_name: ${{ steps.release.outputs.tag_name }}
      version: ${{ steps.release.outputs.version }}
    steps:
      - name: Release Please
        uses: googleapis/release-please-action@v4
        id: release
        with:
          release-type: simple
          token: ${{ secrets.GITHUB_TOKEN }}

  publish:
    needs: release-please
    if: ${{ needs.release-please.outputs.release_created }}
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6

      - uses: actions/setup-dotnet@v5
        with:
          dotnet-version: 10.0.x

      - name: Build
        run: dotnet build -c Release -p:Version=${{ needs.release-please.outputs.version }}

      - name: Test
        run: dotnet test -c Release --no-build

      - name: Pack
        run: |
          dotnet pack src/ZeroAlloc.Outbox/ZeroAlloc.Outbox.csproj -c Release --no-build -p:PackageVersion=${{ needs.release-please.outputs.version }} -o ./nupkg
          dotnet pack src/ZeroAlloc.Outbox.Generator/ZeroAlloc.Outbox.Generator.csproj -c Release --no-build -p:PackageVersion=${{ needs.release-please.outputs.version }} -o ./nupkg
          dotnet pack src/ZeroAlloc.Outbox.EfCore/ZeroAlloc.Outbox.EfCore.csproj -c Release --no-build -p:PackageVersion=${{ needs.release-please.outputs.version }} -o ./nupkg
          dotnet pack src/ZeroAlloc.Outbox.InMemory/ZeroAlloc.Outbox.InMemory.csproj -c Release --no-build -p:PackageVersion=${{ needs.release-please.outputs.version }} -o ./nupkg

      - name: Push to NuGet
        run: dotnet nuget push ./nupkg/*.nupkg --api-key ${{ secrets.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json --skip-duplicate

      - name: Upload to GitHub Release
        run: gh release upload ${{ needs.release-please.outputs.tag_name }} ./nupkg/*.nupkg
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

**Step 2: Create `.github/workflows/release.yml`**

```yaml
name: Release

on:
  release:
    types: [published]

jobs:
  publish:
    runs-on: ubuntu-latest

    steps:
      - name: Checkout
        uses: actions/checkout@v6
        with:
          fetch-depth: 0

      - name: Setup .NET
        uses: actions/setup-dotnet@v5
        with:
          dotnet-version: 10.0.x

      - name: Restore tools
        run: dotnet tool restore

      - name: Extract version from tag
        id: version
        run: |
          TAG="${{ github.event.release.tag_name }}"
          VERSION="${TAG#v}"
          echo "version=$VERSION" >> "$GITHUB_OUTPUT"
          echo "Release version: $VERSION"

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore -c Release -p:Version=${{ steps.version.outputs.version }}

      - name: Test
        run: dotnet test --no-build -c Release --verbosity normal

      - name: Pack
        run: |
          dotnet pack src/ZeroAlloc.Outbox/ZeroAlloc.Outbox.csproj --no-build -c Release -p:PackageVersion=${{ steps.version.outputs.version }} -o ./artifacts
          dotnet pack src/ZeroAlloc.Outbox.Generator/ZeroAlloc.Outbox.Generator.csproj --no-build -c Release -p:PackageVersion=${{ steps.version.outputs.version }} -o ./artifacts
          dotnet pack src/ZeroAlloc.Outbox.EfCore/ZeroAlloc.Outbox.EfCore.csproj --no-build -c Release -p:PackageVersion=${{ steps.version.outputs.version }} -o ./artifacts
          dotnet pack src/ZeroAlloc.Outbox.InMemory/ZeroAlloc.Outbox.InMemory.csproj --no-build -c Release -p:PackageVersion=${{ steps.version.outputs.version }} -o ./artifacts

      - name: Push to NuGet
        run: dotnet nuget push ./artifacts/*.nupkg --api-key ${{ secrets.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json
```

**Step 3: Commit**

```bash
git add .github/workflows/release-please.yml .github/workflows/release.yml
git commit -m "ci: add release-please and release workflows"
```

---

## Task 5: Website trigger workflow

**Files:**
- Create: `.github/workflows/trigger-website.yml`

**Context:** When docs change on `main`, dispatch a `submodule-update` event to the `ZeroAlloc-Net/.website` repo so it can rebuild. Requires a `WEBSITE_DISPATCH_TOKEN` secret with `Actions:write` on that repo.

**Step 1: Create `.github/workflows/trigger-website.yml`**

```yaml
# Requires a secret named WEBSITE_DISPATCH_TOKEN with Actions:write permission on ZeroAlloc-Net/.website
name: Trigger website rebuild

on:
  push:
    branches: [main]
    paths:
      - 'docs/**'

jobs:
  trigger:
    runs-on: ubuntu-latest
    steps:
      - name: Dispatch to .website repo
        uses: peter-evans/repository-dispatch@v3
        with:
          token: ${{ secrets.WEBSITE_DISPATCH_TOKEN }}
          repository: ZeroAlloc-Net/.website
          event-type: submodule-update
```

**Step 2: Commit**

```bash
git add .github/workflows/trigger-website.yml
git commit -m "ci: add trigger-website workflow for docs changes"
```

---

## Task 6: GitHub templates

**Files:**
- Create: `.github/PULL_REQUEST_TEMPLATE.md`
- Create: `.github/ISSUE_TEMPLATE/bug_report.yml`
- Create: `.github/ISSUE_TEMPLATE/feature_request.yml`

**Context:** These provide consistent issue/PR formatting. The PR template drops the sample-app checklist item (Outbox has no sample project) and adapts the scope note for EF Core / InMemory packages. The issue templates are adapted from Mediator with Outbox-specific descriptions.

**Step 1: Create `.github/PULL_REQUEST_TEMPLATE.md`**

```markdown
## Summary

<!-- Brief description of the changes and why they are needed -->

## Type of Change

- [ ] `feat` — New feature
- [ ] `fix` — Bug fix
- [ ] `perf` — Performance improvement
- [ ] `refactor` — Code refactoring (no behavior change)
- [ ] `docs` — Documentation only
- [ ] `test` — Adding or updating tests
- [ ] `build` / `ci` — Build system or CI changes
- [ ] `chore` — Maintenance

## Changes

-

## Breaking Changes

<!-- If this is a breaking change, describe what breaks and the migration path -->

None

## Test Plan

- [ ] All existing tests pass (`dotnet test`)
- [ ] New tests added for new functionality
- [ ] EF Core store tested against a real database where applicable

## Checklist

- [ ] Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/)
- [ ] Code builds without warnings
- [ ] Generator code targets `netstandard2.0` (no C# 10+ features)
- [ ] No secrets or credentials in committed files
```

**Step 2: Create `.github/ISSUE_TEMPLATE/bug_report.yml`**

```yaml
name: Bug Report
description: Report a bug in ZeroAlloc.Outbox
labels: [bug]
body:
  - type: markdown
    attributes:
      value: |
        Thank you for reporting a bug. Please fill out the details below.

  - type: textarea
    id: description
    attributes:
      label: Description
      description: A clear description of the bug.
    validations:
      required: true

  - type: textarea
    id: repro
    attributes:
      label: Steps to Reproduce
      description: Minimal code or steps to reproduce the issue.
      placeholder: |
        ```csharp
        [OutboxMessage]
        public sealed record OrderPlaced(int Id);
        // ...
        ```
    validations:
      required: true

  - type: textarea
    id: expected
    attributes:
      label: Expected Behavior
      description: What you expected to happen.
    validations:
      required: true

  - type: textarea
    id: actual
    attributes:
      label: Actual Behavior
      description: What actually happened. Include compiler errors/warnings if applicable.
    validations:
      required: true

  - type: input
    id: version
    attributes:
      label: ZeroAlloc.Outbox Version
      placeholder: e.g. 0.1.0
    validations:
      required: true

  - type: input
    id: dotnet-version
    attributes:
      label: .NET Version
      placeholder: e.g. 10.0.3

  - type: textarea
    id: additional
    attributes:
      label: Additional Context
      description: Any other context, generated source output, or screenshots.
```

**Step 3: Create `.github/ISSUE_TEMPLATE/feature_request.yml`**

```yaml
name: Feature Request
description: Suggest a feature for ZeroAlloc.Outbox
labels: [enhancement]
body:
  - type: markdown
    attributes:
      value: |
        Thank you for suggesting a feature. Please describe what you'd like.

  - type: textarea
    id: problem
    attributes:
      label: Problem
      description: What problem does this feature solve? What's your use case?
    validations:
      required: true

  - type: textarea
    id: solution
    attributes:
      label: Proposed Solution
      description: How you'd like this to work. Include API examples if possible.
      placeholder: |
        ```csharp
        // Example of how the API would look
        ```
    validations:
      required: true

  - type: textarea
    id: alternatives
    attributes:
      label: Alternatives Considered
      description: Any alternative approaches or workarounds you've considered.

  - type: dropdown
    id: scope
    attributes:
      label: Scope
      options:
        - Core abstractions (ZeroAlloc.Outbox)
        - Source generator (ZeroAlloc.Outbox.Generator)
        - EF Core store (ZeroAlloc.Outbox.EfCore)
        - InMemory store (ZeroAlloc.Outbox.InMemory)
        - Analyzer diagnostics
        - Documentation
        - Other
    validations:
      required: true
```

**Step 4: Commit**

```bash
git add .github/PULL_REQUEST_TEMPLATE.md .github/ISSUE_TEMPLATE/
git commit -m "ci: add PR template and issue templates"
```

---

## Task 7: README overhaul

**Files:**
- Modify: `README.md`

**Context:** Replace the 2-line stub with a full Cache-style README. The pattern is: title + one-paragraph description → badges (4 NuGet + build + license) → `dotnet add package` block → quick start (4 steps) → features table → diagnostics table → documentation link → license line. NuGet badge URLs use `img.shields.io/nuget/v/<package>.svg`. Build badge points to `ci.yml`.

**Step 1: Overwrite `README.md`**

```markdown
# ZeroAlloc.Outbox

Source-generated transactional outbox for .NET. Annotate a message type with `[OutboxMessage]` and a Roslyn source generator emits a typed writer and dispatcher bridge — no reflection, no boxing, AOT-safe. Backed by EF Core (production) or in-memory (tests), with a built-in polling worker, exponential-backoff retry, and dead-letter support.

[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.Outbox.svg)](https://www.nuget.org/packages/ZeroAlloc.Outbox)
[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.Outbox.Generator.svg)](https://www.nuget.org/packages/ZeroAlloc.Outbox.Generator)
[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.Outbox.EfCore.svg)](https://www.nuget.org/packages/ZeroAlloc.Outbox.EfCore)
[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.Outbox.InMemory.svg)](https://www.nuget.org/packages/ZeroAlloc.Outbox.InMemory)
[![Build](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/actions/workflows/ci.yml/badge.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

---

## Install

```bash
# Core abstractions + source generator (always required)
dotnet add package ZeroAlloc.Outbox
dotnet add package ZeroAlloc.Outbox.Generator

# Pick a store:
dotnet add package ZeroAlloc.Outbox.EfCore    # production — Entity Framework Core
dotnet add package ZeroAlloc.Outbox.InMemory  # testing — in-process, no database
```

---

## Quick start

**1. Annotate your message:**

```csharp
using ZeroAlloc.Outbox;

[OutboxMessage]
public sealed record OrderPlaced(int OrderId, decimal Amount);
```

The generator emits `IOutboxWriter<OrderPlaced>` and its DI registration extension.

**2. Register with DI:**

```csharp
builder.Services.AddOutbox(options =>
{
    options.PollingInterval = TimeSpan.FromSeconds(5);
    options.BatchSize       = 50;
    options.MaxAttempts     = 3;
});

builder.Services.AddOutboxEfCore<AppDbContext>();  // or AddOutboxInMemory()
builder.Services.AddOrderPlacedOutbox();           // generated extension
```

**3. Write in a transaction:**

```csharp
public class OrderService(IOutboxWriter<OrderPlaced> writer, AppDbContext db)
{
    public async Task PlaceOrderAsync(Order order, CancellationToken ct)
    {
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);                          // within the same transaction
        await writer.WriteAsync(new OrderPlaced(order.Id, order.Total), ct: ct);
    }
}
```

**4. Implement a dispatcher:**

```csharp
public class OrderPlacedDispatcher(IMessageBus bus) : IOutboxDispatcher<OrderPlaced>
{
    public async Task DispatchAsync(OrderPlaced message, CancellationToken ct)
        => await bus.PublishAsync(message, ct);
}

// Register the dispatcher
builder.Services.AddTransient<IOutboxDispatcher<OrderPlaced>, OrderPlacedDispatcher>();
```

---

## Features

| Feature | Notes |
|---------|-------|
| Source-generated writers | `[OutboxMessage]` triggers generator; typed `IOutboxWriter<T>` emitted at compile time |
| Typed dispatchers | `IOutboxDispatcher<T>` — implement once, wire to any transport (bus, HTTP, email) |
| EF Core store | Writes and reads via `DbContext`; enlist in ambient transaction for atomicity |
| InMemory store | Thread-safe in-process store for unit and integration tests |
| Polling worker | `OutboxWorkerService` (`IHostedService`) polls on configurable interval with scope isolation |
| Exponential backoff | Retry delay = `RetryBaseDelay × 2^(attempt-1)`; configurable via `OutboxOptions` |
| Dead-letter | Entries that exceed `MaxAttempts` are dead-lettered with the failure reason |
| AOT / trimmer safe | All dispatch code is generated; no `Type.GetType`, no `MakeGenericType` |
| `IOptions<OutboxOptions>` | Full options support with hot-reload via standard `Microsoft.Extensions.Options` |

---

## Diagnostics

| ID | Severity | Description |
|----|----------|-------------|
| [ZO0001](docs/diagnostics/ZO0001.md) | Warning | `[OutboxMessage]` applied to an interface — code will not be generated |
| [ZO0002](docs/diagnostics/ZO0002.md) | Warning | `[OutboxMessage]` applied to a static class — code will not be generated |
| [ZO0003](docs/diagnostics/ZO0003.md) | Warning | `[OutboxMessage]` applied to a nested type — use a top-level type for a stable type discriminator |

---

## Documentation

Full docs live in [`docs/`](docs/index.md):

- [Getting Started](docs/getting-started.md)
- [Outbox Pattern](docs/outbox-pattern.md)
- [Message Types](docs/message-types.md)
- [Dispatchers](docs/dispatchers.md)
- [Store Adapters](docs/store-adapters.md)
- [Background Worker](docs/background-worker.md)
- [Dependency Injection](docs/dependency-injection.md)
- Diagnostics: [ZO0001](docs/diagnostics/ZO0001.md) · [ZO0002](docs/diagnostics/ZO0002.md) · [ZO0003](docs/diagnostics/ZO0003.md)

---

## License

MIT
```

**Step 2: Commit**

```bash
git add README.md
git commit -m "docs: overhaul README with badges, quick start, features, and diagnostics"
```

---

## Task 8: Docs index and outbox-pattern overview

**Files:**
- Create: `docs/index.md`
- Create: `docs/outbox-pattern.md`

**Context:** `index.md` is the table of contents for the Docusaurus website. `outbox-pattern.md` explains the transactional outbox pattern itself (the "why") — this is domain documentation, not API documentation. Docusaurus requires `id`, `title`, and `sidebar_position` frontmatter.

**Step 1: Create `docs/index.md`**

```markdown
---
id: index
title: ZeroAlloc.Outbox
sidebar_position: 1
---

# ZeroAlloc.Outbox

Source-generated transactional outbox for .NET. Annotate a message type with `[OutboxMessage]` and a Roslyn source generator emits a typed writer and dispatcher bridge — no reflection, no boxing, AOT-safe. Backed by EF Core or in-memory, with a built-in polling worker, retry, and dead-letter.

## Contents

- [Getting Started](getting-started.md) — install, annotate, register, dispatch
- [Outbox Pattern](outbox-pattern.md) — why the transactional outbox pattern exists
- [Message Types](message-types.md) — `[OutboxMessage]`, generated code, type discriminator
- [Dispatchers](dispatchers.md) — `IOutboxDispatcher<T>`, `DefaultOutboxDispatcher`, custom implementations
- [Store Adapters](store-adapters.md) — EF Core adapter (schema, migration), InMemory (test usage)
- [Background Worker](background-worker.md) — polling, retry back-off, dead-letter, `OutboxOptions` reference
- [Dependency Injection](dependency-injection.md) — `AddOutbox`, `AddOutboxEfCore`, `AddOrderPlacedOutbox`, lifetime rules
- [Diagnostics](diagnostics.md) — ZO0001, ZO0002, ZO0003
- [Performance](performance.md) — zero-alloc design, AOT safety, source-gen vs reflection
- [Testing](testing.md) — `InMemoryOutboxStore`, `AllEntries()`, worker integration tests
- Cookbook
  - [EF Core Transaction](cookbook/01-ef-core-transaction.md)
  - [Custom Dispatcher](cookbook/02-custom-dispatcher.md)
  - [Mediator Integration](cookbook/03-mediator-integration.md)
  - [Dead-Letter Handling](cookbook/04-dead-letter-handling.md)
  - [Testing with Host](cookbook/05-testing-with-host.md)
```

**Step 2: Create `docs/outbox-pattern.md`**

```markdown
---
id: outbox-pattern
title: The Outbox Pattern
sidebar_position: 2
---

# The Outbox Pattern

## The problem

In a distributed system, writing to a database and publishing a message to a broker are two separate operations. If the process crashes after the database write but before the message is published, the downstream consumer never learns about the event. Conversely, if the message is published first and then the database write fails, the consumer acts on data that does not exist.

Both races produce inconsistency that is hard to detect and painful to recover from.

## The solution

The **transactional outbox pattern** solves the problem by treating the message as data:

1. Write your domain change **and** the outgoing message to the same database in the same transaction. Either both commit or both roll back — atomicity guaranteed.
2. A separate background worker polls the outbox table and dispatches each message to its destination (message broker, HTTP endpoint, etc.).
3. Once a message is dispatched successfully, mark it as succeeded. If dispatch fails, retry with back-off until a maximum attempt count is reached, then dead-letter.

The worker uses **at-least-once delivery**: a message may be dispatched more than once (e.g., if the process crashes after dispatch but before the success mark). Downstream consumers should be idempotent, or the outbox entry ID can be used as an idempotency key.

## ZeroAlloc.Outbox implementation

| Concern | How ZeroAlloc.Outbox handles it |
|---------|-------------------------------|
| Atomic write | `IOutboxWriter<T>` enlists in the caller's `DbTransaction` (`EfCore` store) |
| Type safety | `[OutboxMessage]` generates a concrete `IOutboxWriter<T>` — no stringly-typed payloads |
| Polling | `OutboxWorkerService` (`BackgroundService`) polls on a configurable interval |
| Retry | Exponential back-off: `RetryBaseDelay × 2^(attempt-1)` |
| Dead-letter | Entries exceeding `MaxAttempts` are moved to dead-letter state with the failure reason |
| Dispatcher | `IOutboxDispatcher<T>` — you implement one method; the worker handles scheduling |

See [Getting Started](getting-started.md) to set this up in five minutes.
```

**Step 3: Commit**

```bash
git add docs/index.md docs/outbox-pattern.md
git commit -m "docs: add docs index and outbox-pattern overview"
```

---

## Task 9: API reference docs — message types, dispatchers, store adapters

**Files:**
- Create: `docs/message-types.md`
- Create: `docs/dispatchers.md`
- Create: `docs/store-adapters.md`

**Context:** These three pages document the core abstractions. `message-types.md` explains what the generator emits for a `[OutboxMessage]` type. `dispatchers.md` documents `IOutboxDispatcher<T>` and `IOutboxTypeDispatcher`. `store-adapters.md` covers both the EF Core schema/migration and the InMemory store.

**Step 1: Create `docs/message-types.md`**

```markdown
---
id: message-types
title: Message Types
sidebar_position: 4
---

# Message Types

## The `[OutboxMessage]` attribute

Apply `[OutboxMessage]` to any top-level, non-static `class`, `record`, or `struct`:

```csharp
using ZeroAlloc.Outbox;

[OutboxMessage]
public sealed record OrderPlaced(int OrderId, decimal Amount);
```

The Roslyn source generator reads the attribute at compile time and emits two types:

| Generated type | Purpose |
|----------------|---------|
| `OrderPlacedOutboxWriter` | Implements `IOutboxWriter<OrderPlaced>` — serializes and enqueues |
| `AddOrderPlacedOutboxExtensions` | `IServiceCollection` extension that registers the writer and its dispatcher bridge |

## What the generator emits

For a type `MyApp.OrderPlaced` the generator produces (abbreviated):

```csharp
// <auto-generated/>
namespace MyApp;

internal sealed class OrderPlacedOutboxWriter : IOutboxWriter<OrderPlaced>
{
    private readonly IOutboxStore _store;
    private readonly IOutboxSerializer _serializer;

    public OrderPlacedOutboxWriter(IOutboxStore store, IOutboxSerializer serializer)
    {
        _store = store;
        _serializer = serializer;
    }

    public ValueTask WriteAsync(
        OrderPlaced message,
        DbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        var payload = _serializer.Serialize(message);
        return _store.EnqueueAsync("MyApp.OrderPlaced", payload, transaction, ct);
    }
}
```

## Type discriminator

The string `"MyApp.OrderPlaced"` (the fully-qualified type name) is used as the **type discriminator** stored in the `TypeName` column of the outbox table. The worker uses this value to look up the correct `IOutboxTypeDispatcher` at dispatch time.

This is why `[OutboxMessage]` must be applied to **top-level** types: nested types change their fully-qualified name when the outer class is renamed, silently breaking the discriminator for already-stored entries.

## Diagnostics

| ID | Trigger | Effect |
|----|---------|--------|
| [ZO0001](diagnostics/ZO0001.md) | Interface | Warning; no code generated |
| [ZO0002](diagnostics/ZO0002.md) | Static class | Warning; no code generated |
| [ZO0003](diagnostics/ZO0003.md) | Nested type | Warning; no code generated |
```

**Step 2: Create `docs/dispatchers.md`**

```markdown
---
id: dispatchers
title: Dispatchers
sidebar_position: 5
---

# Dispatchers

## `IOutboxDispatcher<T>`

Implement one method to send a deserialized message to its destination:

```csharp
public interface IOutboxDispatcher<T>
{
    Task DispatchAsync(T message, CancellationToken ct);
}
```

Example — publishing to a message broker:

```csharp
public class OrderPlacedDispatcher(IMessageBus bus) : IOutboxDispatcher<OrderPlaced>
{
    public Task DispatchAsync(OrderPlaced message, CancellationToken ct)
        => bus.PublishAsync(message, ct);
}
```

Register it:

```csharp
builder.Services.AddTransient<IOutboxDispatcher<OrderPlaced>, OrderPlacedDispatcher>();
```

## `IOutboxTypeDispatcher` (internal bridge)

The generator also emits an `OrderPlacedOutboxTypeDispatcher` that implements the non-generic `IOutboxTypeDispatcher`. This is the interface the background worker uses internally to dispatch by type name string without reflection. You never implement this interface yourself.

```csharp
// Generated — do not implement manually
internal sealed class OrderPlacedOutboxTypeDispatcher : IOutboxTypeDispatcher
{
    public string TypeName => "MyApp.OrderPlaced";

    public Task DispatchAsync(byte[] payload, CancellationToken ct)
    {
        var message = _serializer.Deserialize<OrderPlaced>(payload);
        return _dispatcher.DispatchAsync(message, ct);
    }
}
```

## Custom dispatchers

Any transport works. Examples:

| Transport | Pattern |
|-----------|---------|
| Message broker | Inject `IMessageBus` / `IPublisher` and publish |
| HTTP | Inject `HttpClient` and `POST` |
| Email | Inject `IEmailSender` and send |
| ZeroAlloc.Mediator | Inject `IMediator` and `SendAsync` / `PublishAsync` |

See [Mediator Integration](cookbook/03-mediator-integration.md) for the last case.
```

**Step 3: Create `docs/store-adapters.md`**

```markdown
---
id: store-adapters
title: Store Adapters
sidebar_position: 6
---

# Store Adapters

## EF Core store (`ZeroAlloc.Outbox.EfCore`)

### Registration

```csharp
builder.Services.AddOutboxEfCore<AppDbContext>();
```

This registers `EfCoreOutboxStore` as `IOutboxStore` (scoped) and wires the `OutboxMessageEntity` configuration into the provided `DbContext`.

### Schema

A single table `OutboxMessages` is added with these columns:

| Column | Type | Notes |
|--------|------|-------|
| `Id` | `Guid` | Primary key, generated client-side (`Guid.NewGuid()`) |
| `TypeName` | `nvarchar(500)` | Fully-qualified type discriminator |
| `Payload` | `varbinary(max)` | Serialized message bytes |
| `Status` | `int` | `0` = Pending, `1` = Succeeded, `2` = DeadLettered |
| `RetryCount` | `int` | Number of failed dispatch attempts |
| `CreatedAt` | `datetimeoffset` | UTC time of enqueue |
| `NextRetryAt` | `datetimeoffset?` | Earliest time to retry (null = immediately eligible) |
| `ErrorMessage` | `nvarchar(2000)?` | Last failure reason or dead-letter reason |

An index on `(Status, NextRetryAt)` covers the `FetchPendingAsync` query.

### Migration

The store does not auto-migrate. Add a migration after adding the store:

```bash
dotnet ef migrations add AddOutboxMessages --project src/YourProject.EfCore
dotnet ef database update
```

### Transactional enqueue

Pass the ambient `DbTransaction` to `IOutboxWriter<T>.WriteAsync` to enlist the enqueue in the caller's transaction:

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);
db.Orders.Add(order);
await db.SaveChangesAsync(ct);
await writer.WriteAsync(new OrderPlaced(order.Id), tx.GetDbTransaction(), ct);
await tx.CommitAsync(ct);
```

See [EF Core Transaction](cookbook/01-ef-core-transaction.md) for the complete pattern.

---

## InMemory store (`ZeroAlloc.Outbox.InMemory`)

### Registration

```csharp
builder.Services.AddOutboxInMemory();
```

Registers `InMemoryOutboxStore` as both `IOutboxStore` and `InMemoryOutboxStore` (singleton) so tests can inspect entries directly.

### Test usage

```csharp
var store = host.Services.GetRequiredService<InMemoryOutboxStore>();

// Assert entry was written
store.AllEntries().Should().ContainSingle();

// Assert dispatched
store.AllEntries().Single().Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);
```

The `AllEntries()` method returns a snapshot — it does not lock the store, so call it after the worker has had time to process.

See [Testing](testing.md) for integration test patterns using `HostBuilder` + InMemory store.
```

**Step 4: Commit**

```bash
git add docs/message-types.md docs/dispatchers.md docs/store-adapters.md
git commit -m "docs: add message-types, dispatchers, and store-adapters pages"
```

---

## Task 10: Background worker, DI, and getting-started update

**Files:**
- Create: `docs/background-worker.md`
- Create: `docs/dependency-injection.md`
- Modify: `docs/getting-started.md` — add Docusaurus frontmatter at the top

**Context:** `background-worker.md` documents the polling loop, retry formula, dead-letter behaviour, and the full `OutboxOptions` reference table. `dependency-injection.md` explains all DI registrations and lifetime rules. `getting-started.md` already exists but needs Docusaurus frontmatter added to its top.

**Step 1: Create `docs/background-worker.md`**

```markdown
---
id: background-worker
title: Background Worker
sidebar_position: 7
---

# Background Worker

## Overview

`OutboxWorkerService` is an `IHostedService` (specifically `BackgroundService`) registered by `AddOutbox()`. It runs a polling loop:

1. Create a fresh DI scope (isolates EF Core `DbContext` per batch).
2. Fetch up to `BatchSize` pending entries whose `NextRetryAt` is `null` or in the past.
3. For each entry, look up the registered `IOutboxTypeDispatcher` by `TypeName`.
4. Dispatch. On success mark the entry `Succeeded`. On failure increment `RetryCount` and schedule the next retry or dead-letter.
5. Sleep for `PollingInterval` and repeat.

## Retry back-off

The retry delay is calculated as:

```
delay = RetryBaseDelay × 2^(attempt - 1)
```

| Attempt | `RetryBaseDelay = 1 s` | `RetryBaseDelay = 5 s` |
|---------|------------------------|------------------------|
| 1 | 1 s | 5 s |
| 2 | 2 s | 10 s |
| 3 | 4 s | 20 s |
| 4 | 8 s | 40 s |

When `RetryCount` reaches `MaxAttempts` the entry is dead-lettered with the last exception message.

## Dead-letter

Dead-lettered entries have `Status = DeadLettered` and a non-null `ErrorMessage`. The worker does not retry them. To requeue a dead-lettered entry, reset `Status = Pending` and `RetryCount = 0` directly in the database (or via your own management tooling).

An entry is also dead-lettered immediately (attempt 0) if no `IOutboxTypeDispatcher` is registered for its `TypeName`. This prevents the worker from endlessly re-fetching an unroutable entry.

## `OutboxOptions` reference

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `PollingInterval` | `TimeSpan` | `00:00:05` | How long the worker sleeps between batch cycles |
| `BatchSize` | `int` | `50` | Maximum number of entries fetched per cycle |
| `MaxAttempts` | `int` | `5` | Dispatch attempts before dead-lettering |
| `RetryBaseDelay` | `TimeSpan` | `00:00:01` | Base delay for exponential back-off calculation |

Configure via `AddOutbox`:

```csharp
builder.Services.AddOutbox(options =>
{
    options.PollingInterval = TimeSpan.FromSeconds(10);
    options.BatchSize       = 100;
    options.MaxAttempts     = 3;
    options.RetryBaseDelay  = TimeSpan.FromSeconds(5);
});
```
```

**Step 2: Create `docs/dependency-injection.md`**

```markdown
---
id: dependency-injection
title: Dependency Injection
sidebar_position: 8
---

# Dependency Injection

## `AddOutbox`

```csharp
builder.Services.AddOutbox();
// or
builder.Services.AddOutbox(options => { options.BatchSize = 100; });
```

Registers:

| Service | Lifetime | Notes |
|---------|----------|-------|
| `IOptions<OutboxOptions>` | Singleton | Bound from `OutboxOptions` section or inline configuration |
| `OutboxWorkerService` | Singleton | `IHostedService`; polls the store in a background loop |
| `SystemTextJsonOutboxSerializer` | Singleton | Default `IOutboxSerializer` implementation |

## `AddOutboxEfCore<TContext>`

```csharp
builder.Services.AddOutboxEfCore<AppDbContext>();
```

Registers:

| Service | Lifetime | Notes |
|---------|----------|-------|
| `EfCoreOutboxStore` as `IOutboxStore` | Scoped | Scoped to match `DbContext` lifetime |

The `TContext` type must have `OutboxMessageEntity` configured (done automatically by `AddOutboxEfCore`).

## `AddOutboxInMemory`

```csharp
builder.Services.AddOutboxInMemory();
```

Registers:

| Service | Lifetime | Notes |
|---------|----------|-------|
| `InMemoryOutboxStore` as `IOutboxStore` | Singleton | Thread-safe; singleton so tests share the same instance |
| `InMemoryOutboxStore` (concrete) | Singleton | Same instance; lets tests call `AllEntries()` directly |

## Generated `AddXxxOutbox` extension

The source generator emits one extension per `[OutboxMessage]` type, e.g. for `OrderPlaced`:

```csharp
public static IServiceCollection AddOrderPlacedOutbox(this IServiceCollection services)
{
    services.AddTransient<IOutboxWriter<OrderPlaced>, OrderPlacedOutboxWriter>();
    services.AddTransient<IOutboxTypeDispatcher, OrderPlacedOutboxTypeDispatcher>();
    return services;
}
```

The `IOutboxTypeDispatcher` is registered as `Transient` so it is resolved fresh inside each scope the worker creates per batch cycle.

## Lifetime rules

- `IOutboxStore` implementations must be **Scoped** (EF Core) or **Singleton** (InMemory). Do not register a Singleton EF Core store — it captures the `DbContext` and causes data corruption.
- `IOutboxDispatcher<T>` implementations can be any lifetime. `Transient` is the safest default.
- The worker creates a new `IServiceScope` per batch cycle, so Scoped services are correctly isolated.
```

**Step 3: Add frontmatter to `docs/getting-started.md`**

Read the existing file first, then prepend the frontmatter block. The frontmatter to add at the top:

```markdown
---
id: getting-started
title: Getting Started
sidebar_position: 3
---

```

(The rest of the file content stays unchanged.)

**Step 4: Commit**

```bash
git add docs/background-worker.md docs/dependency-injection.md docs/getting-started.md
git commit -m "docs: add background-worker, dependency-injection, and frontmatter to getting-started"
```

---

## Task 11: Diagnostics summary page and ZO000x detail pages

**Files:**
- Create: `docs/diagnostics.md`
- Create: `docs/diagnostics/ZO0001.md`
- Create: `docs/diagnostics/ZO0002.md`
- Create: `docs/diagnostics/ZO0003.md`

**Context:** `diagnostics.md` is a summary table. The three detail pages follow the pattern from `ZeroAlloc.Cache/docs/diagnostics/ZC0001.md`: frontmatter, severity, explanation, example, how to fix (option A / option B), when to suppress. Use Docusaurus frontmatter.

**Step 1: Create `docs/diagnostics.md`**

```markdown
---
id: diagnostics
title: Diagnostics
sidebar_position: 9
---

# Diagnostics

The `ZeroAlloc.Outbox.Generator` analyzer emits the following diagnostics at compile time.

| ID | Severity | Description |
|----|----------|-------------|
| [ZO0001](diagnostics/ZO0001.md) | Warning | `[OutboxMessage]` applied to an interface — no code is generated |
| [ZO0002](diagnostics/ZO0002.md) | Warning | `[OutboxMessage]` applied to a static class — no code is generated |
| [ZO0003](diagnostics/ZO0003.md) | Warning | `[OutboxMessage]` applied to a nested type — use a top-level type for a stable type discriminator |

All diagnostics are enabled by default. They are warnings, not errors, so the build succeeds but you will not get the generated writer.
```

**Step 2: Create `docs/diagnostics/ZO0001.md`**

```markdown
---
id: ZO0001
title: ZO0001 — OutboxMessage on Interface
sidebar_position: 1
---

# ZO0001 — OutboxMessage on Interface

**Severity:** Warning

`[OutboxMessage]` was applied to an interface. The generator only emits code for concrete types (`class`, `record`, `struct`). No `IOutboxWriter<T>` or `IOutboxTypeDispatcher` is generated for this interface.

---

## Example

```csharp
// ZO0001: [OutboxMessage] must be applied to a class, record, or struct
[OutboxMessage]
public interface IOrderPlaced { }
```

---

## How to fix

**Option A — Change to a record (recommended):**

```csharp
[OutboxMessage]
public sealed record OrderPlaced(int OrderId, decimal Amount);
```

**Option B — Change to a class:**

```csharp
[OutboxMessage]
public sealed class OrderPlaced
{
    public int OrderId { get; init; }
    public decimal Amount { get; init; }
}
```

---

## When to suppress

Never. An interface cannot be instantiated, so no writer can be generated for it. If you intended to annotate a concrete implementation, move the attribute there.
```

**Step 3: Create `docs/diagnostics/ZO0002.md`**

```markdown
---
id: ZO0002
title: ZO0002 — OutboxMessage on Static Class
sidebar_position: 2
---

# ZO0002 — OutboxMessage on Static Class

**Severity:** Warning

`[OutboxMessage]` was applied to a static class. Static classes cannot be instantiated or serialized, so no `IOutboxWriter<T>` or `IOutboxTypeDispatcher` is generated.

---

## Example

```csharp
// ZO0002: [OutboxMessage] cannot be applied to a static class
[OutboxMessage]
public static class OrderPlacedMessages { }
```

---

## How to fix

Remove `static` from the class declaration:

```csharp
[OutboxMessage]
public sealed class OrderPlaced
{
    public int OrderId { get; init; }
}
```

Or use a `record`:

```csharp
[OutboxMessage]
public sealed record OrderPlaced(int OrderId);
```

---

## When to suppress

Never. A static class cannot be used as a message payload.
```

**Step 4: Create `docs/diagnostics/ZO0003.md`**

```markdown
---
id: ZO0003
title: ZO0003 — OutboxMessage on Nested Type
sidebar_position: 3
---

# ZO0003 — OutboxMessage on Nested Type

**Severity:** Warning

`[OutboxMessage]` was applied to a type that is declared inside another type (a nested type). Nested types are not supported because their fully-qualified name includes the outer class name. If the outer class is ever renamed, the type discriminator stored in the database changes, silently breaking dispatch for already-stored outbox entries.

---

## Example

```csharp
public class OrderAggregate
{
    // ZO0003: OrderPlaced is a nested type
    [OutboxMessage]
    public sealed record OrderPlaced(int OrderId);
}
```

---

## How to fix

Move the type to the top level of its namespace:

```csharp
namespace MyApp.Orders;

[OutboxMessage]
public sealed record OrderPlaced(int OrderId);
```

---

## When to suppress

In practice, never. The discriminator is stored in the database alongside each outbox entry. Renaming the outer class after messages are in flight is a breaking change that cannot be recovered from without a data migration.

If you have a controlled environment where the outer class name will never change and you accept the coupling risk, you may suppress with:

```csharp
#pragma warning disable ZO0003
[OutboxMessage]
public sealed record OrderPlaced(int OrderId);
#pragma warning restore ZO0003
```
```

**Step 5: Commit**

```bash
git add docs/diagnostics.md docs/diagnostics/
git commit -m "docs: add diagnostics summary and ZO0001/ZO0002/ZO0003 detail pages"
```

---

## Task 12: Performance and testing docs

**Files:**
- Create: `docs/performance.md`
- Create: `docs/testing.md`

**Context:** `performance.md` explains the zero-alloc design rationale, AOT safety, and how source generation avoids reflection. `testing.md` covers InMemory store patterns, `AllEntries()`, and worker integration test setup with `HostBuilder`.

**Step 1: Create `docs/performance.md`**

```markdown
---
id: performance
title: Performance
sidebar_position: 10
---

# Performance

## Zero-allocation design

The "ZeroAlloc" name refers to the dispatch path:

- The `IOutboxTypeDispatcher` implementation is generated at compile time — no `Dictionary<string, Delegate>`, no `MethodInfo.Invoke`, no generic instantiation at runtime.
- The type discriminator (`TypeName` string) is a compile-time constant in the generated class — no string formatting on the hot path.
- Deserialization still allocates (the message object itself is a heap allocation), but the infrastructure around it does not.

## AOT / trimmer safety

All dispatch code paths are:
- Concrete types (no `Type.GetType(string)`, no `MakeGenericType`)
- Registered through standard DI (`services.AddTransient<IOutboxTypeDispatcher, ...>`)
- Compatible with `PublishAot=true` and `TrimmerRootDescriptor` trimming

The `[OutboxMessage]` attribute and the generator are pure Roslyn — there is no runtime reflection.

## Source generation vs reflection

| Concern | Reflection-based | ZeroAlloc.Outbox (generated) |
|---------|-----------------|------------------------------|
| Dispatcher lookup | `Dictionary<Type, MethodInfo>` | `Dictionary<string, IOutboxTypeDispatcher>` (value-type entries) |
| Deserialization call | `MethodInfo.MakeGenericMethod(...).Invoke(...)` | Direct generic call in generated class |
| DI registration | `services.AddTransient(typeof(IDispatcher<>), ...)` | `services.AddTransient<IOutboxTypeDispatcher, OrderPlacedTypeDispatcher>()` |
| AOT safe | No | Yes |
| IL trimmer safe | No | Yes |

## Worker overhead

The worker creates one `IServiceScope` per batch cycle, not per entry. For a batch of 50 entries there is one scope creation and one `FetchPendingAsync` query regardless of batch size.
```

**Step 2: Create `docs/testing.md`**

```markdown
---
id: testing
title: Testing
sidebar_position: 11
---

# Testing

## InMemory store

`ZeroAlloc.Outbox.InMemory` provides `InMemoryOutboxStore` — a thread-safe, in-process outbox store with no database dependency.

### Basic assertion pattern

```csharp
// After writing a message
await writer.WriteAsync(new OrderPlaced(42, 99.99m));

var store = services.GetRequiredService<InMemoryOutboxStore>();

// Assert the entry was written
store.AllEntries().Should().ContainSingle()
    .Which.TypeName.Should().Be("MyApp.OrderPlaced");
```

### Asserting dispatch

```csharp
// After the worker has run
store.AllEntries().Should().ContainSingle()
    .Which.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);
```

### Asserting dead-letter

```csharp
store.AllEntries().Should().ContainSingle()
    .Which.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.DeadLettered);
```

## Integration test with `HostBuilder`

Use a real hosted worker to test the full pipeline:

```csharp
[Fact]
public async Task OrderPlaced_IsDispatched()
{
    var dispatched = new List<OrderPlaced>();

    using var host = await new HostBuilder()
        .ConfigureServices(services =>
        {
            services.AddOutbox(o => { o.PollingInterval = TimeSpan.FromMilliseconds(50); });
            services.AddOutboxInMemory();
            services.AddOrderPlacedOutbox();
            services.AddTransient<IOutboxDispatcher<OrderPlaced>>(
                _ => new DelegateDispatcher<OrderPlaced>(msg =>
                {
                    dispatched.Add(msg);
                    return Task.CompletedTask;
                }));
        })
        .StartAsync();

    var writer = host.Services.GetRequiredService<IOutboxWriter<OrderPlaced>>();
    await writer.WriteAsync(new OrderPlaced(1, 50m));

    await Task.Delay(200); // let the worker poll

    dispatched.Should().ContainSingle().Which.OrderId.Should().Be(1);

    var store = host.Services.GetRequiredService<InMemoryOutboxStore>();
    store.AllEntries().Single().Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);

    await host.StopAsync();
}
```

See [Testing with Host](cookbook/05-testing-with-host.md) for a full helper class that encapsulates the setup.

## Testing without the worker

If you want to test only the write side (not dispatch), skip `AddOutbox()` and call `store.FetchPendingAsync` + your dispatcher manually, or simply inspect `AllEntries()`.
```

**Step 3: Commit**

```bash
git add docs/performance.md docs/testing.md
git commit -m "docs: add performance and testing pages"
```

---

## Task 13: Cookbook — EF Core transaction and custom dispatcher

**Files:**
- Create: `docs/cookbook/01-ef-core-transaction.md`
- Create: `docs/cookbook/02-custom-dispatcher.md`

**Context:** These two recipes are the most commonly needed. Recipe 01 shows the exact pattern for writing an outbox message in the same EF Core transaction as the domain change. Recipe 02 shows how to implement `IOutboxDispatcher<T>` for a custom transport.

**Step 1: Create `docs/cookbook/01-ef-core-transaction.md`**

```markdown
---
id: 01-ef-core-transaction
title: EF Core Transaction
sidebar_position: 1
---

# EF Core Transaction

Write an outbox message in the same `DbTransaction` as your domain change so both commit or both roll back atomically.

## Pattern

```csharp
public class OrderService(
    AppDbContext db,
    IOutboxWriter<OrderPlaced> writer)
{
    public async Task PlaceOrderAsync(NewOrder request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var order = new Order { /* ... */ };
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);

            // Enlist the outbox write in the same transaction
            await writer.WriteAsync(
                new OrderPlaced(order.Id, order.Total),
                tx.GetDbTransaction(),
                ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
```

## Key points

- Pass `tx.GetDbTransaction()` as the second argument to `WriteAsync`. The EF Core store uses `UseTransactionAsync` to enlist in the caller's transaction.
- If `SaveChangesAsync` or `WriteAsync` throws, the `catch` block rolls back both the order row and the outbox entry.
- The outbox worker will not see the entry until the transaction commits, so there is no risk of dispatching a message for a rolled-back domain change.

## Without an explicit transaction

If you do not pass a transaction, the store writes in its own implicit transaction:

```csharp
// This is NOT atomic — order and outbox are separate transactions
db.Orders.Add(order);
await db.SaveChangesAsync(ct);
await writer.WriteAsync(new OrderPlaced(order.Id, order.Total), ct: ct);
```

Use the explicit transaction pattern for production code.
```

**Step 2: Create `docs/cookbook/02-custom-dispatcher.md`**

```markdown
---
id: 02-custom-dispatcher
title: Custom Dispatcher
sidebar_position: 2
---

# Custom Dispatcher

Implement `IOutboxDispatcher<T>` to send messages to any destination.

## HTTP dispatcher example

```csharp
public class OrderPlacedHttpDispatcher(HttpClient http) : IOutboxDispatcher<OrderPlaced>
{
    public async Task DispatchAsync(OrderPlaced message, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/webhooks/order-placed", message, ct);
        response.EnsureSuccessStatusCode();
    }
}
```

Register with a named `HttpClient`:

```csharp
builder.Services.AddHttpClient<OrderPlacedHttpDispatcher>(c =>
{
    c.BaseAddress = new Uri("https://downstream.example.com");
});
builder.Services.AddTransient<IOutboxDispatcher<OrderPlaced>, OrderPlacedHttpDispatcher>();
```

## Email dispatcher example

```csharp
public class OrderPlacedEmailDispatcher(IEmailSender email) : IOutboxDispatcher<OrderPlaced>
{
    public Task DispatchAsync(OrderPlaced message, CancellationToken ct)
        => email.SendAsync(
            to: "ops@example.com",
            subject: $"Order {message.OrderId} placed",
            body: $"Amount: {message.Amount:C}",
            ct);
}
```

## Idempotency

The outbox worker uses **at-least-once delivery**. If the process crashes after dispatch but before the success mark is written, the message will be dispatched again on the next poll. Implement idempotency in your dispatcher or downstream consumer if duplicates are a concern.

A common pattern is to use the outbox entry ID (available via a custom `IOutboxDispatcher<T>` that takes `OutboxEntry` directly) or to include a unique correlation ID in the message payload.
```

**Step 3: Commit**

```bash
git add docs/cookbook/01-ef-core-transaction.md docs/cookbook/02-custom-dispatcher.md
git commit -m "docs(cookbook): add EF Core transaction and custom dispatcher recipes"
```

---

## Task 14: Cookbook — Mediator integration, dead-letter, testing with host

**Files:**
- Create: `docs/cookbook/03-mediator-integration.md`
- Create: `docs/cookbook/04-dead-letter-handling.md`
- Create: `docs/cookbook/05-testing-with-host.md`

**Context:** These three recipes complete the cookbook. Recipe 03 uses `ZeroAlloc.Mediator` as the dispatcher backend. Recipe 04 shows how to inspect dead-lettered entries and requeue them. Recipe 05 provides a reusable `OutboxTestHost` helper class.

**Step 1: Create `docs/cookbook/03-mediator-integration.md`**

```markdown
---
id: 03-mediator-integration
title: Mediator Integration
sidebar_position: 3
---

# Mediator Integration

Use `ZeroAlloc.Mediator` as the transport for outbox dispatchers.

## Setup

Add both packages:

```bash
dotnet add package ZeroAlloc.Outbox
dotnet add package ZeroAlloc.Outbox.Generator
dotnet add package ZeroAlloc.Outbox.EfCore
dotnet add package ZeroAlloc.Mediator
dotnet add package ZeroAlloc.Mediator.Generator
```

## Message as a notification

Annotate the type as both an outbox message and a Mediator notification:

```csharp
[OutboxMessage]
public sealed record OrderPlaced(int OrderId, decimal Amount) : INotification;
```

## Dispatcher

```csharp
public class OrderPlacedDispatcher(IMediator mediator) : IOutboxDispatcher<OrderPlaced>
{
    public Task DispatchAsync(OrderPlaced message, CancellationToken ct)
        => mediator.PublishAsync(message, ct);
}
```

## Registration

```csharp
builder.Services.AddOutbox();
builder.Services.AddOutboxEfCore<AppDbContext>();
builder.Services.AddOrderPlacedOutbox();
builder.Services.AddTransient<IOutboxDispatcher<OrderPlaced>, OrderPlacedDispatcher>();

builder.Services.AddMediator(); // ZeroAlloc.Mediator
```

Handlers registered with Mediator are called when the outbox worker dispatches the message — after the original transaction commits.
```

**Step 2: Create `docs/cookbook/04-dead-letter-handling.md`**

```markdown
---
id: 04-dead-letter-handling
title: Dead-Letter Handling
sidebar_position: 4
---

# Dead-Letter Handling

## What is a dead-lettered entry?

An outbox entry is dead-lettered when:
- The dispatcher throws an exception on `MaxAttempts` consecutive attempts.
- No `IOutboxTypeDispatcher` is registered for the entry's `TypeName`.

Dead-lettered entries have `Status = 2` (`DeadLettered`) and a non-null `ErrorMessage`. The worker never re-fetches them.

## Inspecting dead-lettered entries (EF Core)

Query the outbox table directly:

```csharp
var deadLettered = await db.Set<OutboxMessageEntity>()
    .Where(e => e.Status == OutboxMessageStatus.DeadLettered)
    .OrderBy(e => e.CreatedAt)
    .ToListAsync(ct);
```

## Requeuing a dead-lettered entry

Reset the entry so the worker picks it up again:

```csharp
var entry = await db.Set<OutboxMessageEntity>().FindAsync([id], ct)
    ?? throw new KeyNotFoundException(id.ToString());

entry.Status     = OutboxMessageStatus.Pending;
entry.RetryCount = 0;
entry.NextRetryAt = null;
entry.ErrorMessage = null;

await db.SaveChangesAsync(ct);
```

## Alerting

Hook into your monitoring stack by querying dead-lettered entries on a schedule:

```csharp
// Example: periodic health check that alerts if dead-lettered count > 0
var count = await db.Set<OutboxMessageEntity>()
    .CountAsync(e => e.Status == OutboxMessageStatus.DeadLettered, ct);

if (count > 0)
    logger.LogError("Outbox has {Count} dead-lettered message(s). Manual intervention required.", count);
```

## InMemory dead-letter (tests)

```csharp
store.AllEntries()
    .Where(e => e.Status == InMemoryOutboxStore.InMemoryEntryStatus.DeadLettered)
    .Should().BeEmpty("no messages should be dead-lettered");
```
```

**Step 3: Create `docs/cookbook/05-testing-with-host.md`**

```markdown
---
id: 05-testing-with-host
title: Testing with Host
sidebar_position: 5
---

# Testing with Host

Run the full outbox pipeline — writer, worker, and dispatcher — in an integration test using `HostBuilder` and the InMemory store.

## Reusable helper

```csharp
public sealed class OutboxTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    public IServiceProvider Services => _host.Services;

    public InMemoryOutboxStore Store =>
        _host.Services.GetRequiredService<InMemoryOutboxStore>();

    private OutboxTestHost(IHost host) => _host = host;

    public static async Task<OutboxTestHost> StartAsync(
        Action<IServiceCollection> configure,
        TimeSpan? pollingInterval = null)
    {
        var host = await new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddOutbox(o =>
                {
                    o.PollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(50);
                });
                services.AddOutboxInMemory();
                configure(services);
            })
            .StartAsync();

        return new OutboxTestHost(host);
    }

    public Task WaitForDispatchAsync(TimeSpan? timeout = null)
        => Task.Delay(timeout ?? TimeSpan.FromMilliseconds(300));

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
```

## Example test

```csharp
[Fact]
public async Task OrderPlaced_IsDispatchedSuccessfully()
{
    var dispatched = new List<OrderPlaced>();

    await using var host = await OutboxTestHost.StartAsync(services =>
    {
        services.AddOrderPlacedOutbox();
        services.AddTransient<IOutboxDispatcher<OrderPlaced>>(
            _ => new DelegateDispatcher<OrderPlaced>(msg =>
            {
                dispatched.Add(msg);
                return Task.CompletedTask;
            }));
    });

    var writer = host.Services.GetRequiredService<IOutboxWriter<OrderPlaced>>();
    await writer.WriteAsync(new OrderPlaced(42, 99.99m));

    await host.WaitForDispatchAsync();

    dispatched.Should().ContainSingle().Which.OrderId.Should().Be(42);
    host.Store.AllEntries().Single().Status
        .Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);
}
```

## `DelegateDispatcher<T>` helper

```csharp
public sealed class DelegateDispatcher<T>(Func<T, Task> handler) : IOutboxDispatcher<T>
{
    public Task DispatchAsync(T message, CancellationToken ct) => handler(message);
}
```
```

**Step 4: Commit**

```bash
git add docs/cookbook/
git commit -m "docs(cookbook): add mediator-integration, dead-letter, and testing-with-host recipes"
```

---

## Task 15: Verify everything and finish the branch

**Context:** Run the tests to confirm nothing broke, then finish the development branch.

**Step 1: Run tests**

```bash
dotnet test --verbosity normal
```

Expected: All tests pass (0 failures, 0 warnings from build).

**Step 2: If tests fail**

Read the error output and fix. Do not proceed until tests are green.

**Step 3: Use `superpowers:finishing-a-development-branch`**

Invoke the finishing-a-development-branch skill to present the four completion options and handle the chosen workflow.
