# SonarQube

Code quality runs against a self-hosted **SonarQube Server 26.10 (Community Build)** at
<https://sonarqube.verduguez.com>, project key `overlyric`. There is no cloud organization and no
license: one long-lived branch (`master`), no pull-request decoration, no Sonar Vortex.

## Running an analysis

```powershell
dotnet tool install --global dotnet-sonarscanner --version 12.0.0   # once per machine
$env:SONARQUBE_TOKEN = 'squ_...'                                    # My Account > Security, a user token

dotnet-sonarscanner begin /k:overlyric /v:0.3.1 "/s:$PWD\SonarQube.Analysis.xml" /d:sonar.token=$env:SONARQUBE_TOKEN
dotnet build src\Overlyric\Overlyric.csproj -c Debug
dotnet-sonarscanner end /d:sonar.token=$env:SONARQUBE_TOKEN
```

Three commands, the same three the Sonar documentation shows for .NET. What differs from a
JavaScript or Python project is where the settings live and what counts as "scanning":

- **No `sonar-project.properties`.** The SonarScanner for .NET does not read it: run `begin` without
  `/k:` and it stops with `A required argument is missing: /key:` before it opens any properties
  file. Its supported equivalent is [`SonarQube.Analysis.xml`](../SonarQube.Analysis.xml), passed
  with `/s:` (an absolute path). Server URL, encoding and exclusions live there; the project key
  and the version stay on the command line because the scanner insists on them there; the token
  comes from the environment so it is never committed.
- **The build is the analysis.** C# is not scanned as files: the scanner hooks MSBuild and gives the
  Roslyn analyzer the compilation it sees. A failed build aborts the run, and a partial compilation
  would report partial code.
- **The version comes from the csproj** (`<Version>0.3.1</Version>`). Keeping `/v:` in step with it
  is what makes the dashboard history line up with the installers built from the same commit.

`dotnet-sonarscanner` rather than `dotnet sonarscanner`: the global tool is the supported install
here, because this repository lives on `H:\`, which Windows reports as a remote drive — a local tool
manifest (`dotnet tool restore`) is refused there with a "came from another computer" warning.

For CI, add `/d:sonar.qualitygate.wait=true` to `begin` so a failing gate fails the job.

## What is in scope

Only what the solution compiles: `src/Overlyric`, 49 source files, ~12.3k non-comment lines. The
exclusions in `SonarQube.Analysis.xml` cover everything else — `probe/` (the experiments, not in
`Overlyric.slnx`), `docs/`, `installer/`, `tools/`, `dist/`, `.dsh-tmp/`, `bin/` and `obj/`.
Without them the project would list those files as "not analyzed" and their lines would count
against it.

## Coverage

Nothing is imported yet, and the reason is structural rather than an oversight: `probe\` holds
console programs with hand-written checks (ClockTests, LyricsTests, ResilienceTests, ShortcutsTests
…), not a test framework. `dotnet test` has nothing to run, so there is no `.trx`, no Cobertura
file, and SonarQube shows 0.0 % coverage.

To close that gap, collect coverage around the probes and merge it into one Cobertura file:

```powershell
dotnet tool install --global dotnet-coverage
dotnet-coverage collect -f cobertura -o coverage\probes.xml 'dotnet run --project probe\LyricsTests'
# …repeat per probe, then merge, then reference it from the analysis:
#   /d:sonar.cs.cobertura.reportsPaths=coverage\probes.xml
```

Until then the default quality gate ("Sonar way") will fail its *Coverage on New Code* condition
for any change that adds code. Two honest options: import the coverage above, or relax that one
condition for the project. Do not silence it with `sonar.coverage.exclusions` — that hides the
number rather than measuring it.

## Quality gate and new code

The instance's default gate is `Sonar way`, and the default new-code definition is *previous
version*. The first analysis of a version has no previous version to diff against, so the whole
codebase is new code for that run.

The first full analysis (0.3.1, 49 files) reported 74 open issues: 51 `SYSLIB1054` (use
`LibraryImport` instead of `DllImport`), 6 `S1244` (exact floating-point comparison) and 17 spread
over `CA1822`, `CA1859`, `CA1806`, `CA1068`, `S6640`, `S1751` and `S8949`. All of them were fixed
in code - no issue was marked accepted or false positive - and the 0.3.1 analysis of 26 October 2026
reports 0 open issues and 0 new violations. The gate is now red only on *Coverage on New Code*
(0 %), the gap described above.

## Agents (DSH)

DSH reaches the instance through the **Docker MCP Toolkit gateway**, not by launching the SonarQube
container itself. Docker Desktop holds the server credentials (MCP Toolkit → SonarQube: URL plus a
user token); DSH only runs the gateway over stdio and receives every tool the toolkit exposes.

`~/.dsh/profiles/desktop/cordis.patch.yml`:

```yaml
- insert:
    - id: mcp-docker
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: docker
        transport: stdio
        command: docker
        args: [ 'mcp', 'gateway', 'run', '--profile', 'global' ]
        toolCallTimeoutMs: 120000
```

The tools arrive under the gateway's namespace, `mcp__docker__*`:

```
mcp__docker__get_project_quality_gate_status    gate state for the branch
mcp__docker__search_sonar_issues_in_projects    issues, filterable by file and severity
mcp__docker__get_component_measures             metrics (complexity, duplication, coverage)
mcp__docker__analyze_code_snippet               per-file analysis, no full build needed
```

Docker Desktop has to be running when DSH starts, and the `global` profile is shared with every
other MCP server registered there. `vortex` is absent on purpose: it needs a SonarQube Server
Enterprise/Data Center subscription, and this instance is Community Build.

### Running a scan from a session

The three commands above need a build, so the session needs write access to
`src\Overlyric\bin` and `obj`; under the default `workspace-write` file policy the sandbox denies
both (the directories' ACL grants the interactive user, not the restricted token) and .NET cannot
acquire TLS credentials (`SEC_E_NO_CREDENTIALS`), so the scan has to run from a normal terminal.
With `danger-full-access` it runs from a session unchanged. The MCP tools are unaffected either
way — they execute in the container.

## Continuous integration

The repository has no remote yet (`git remote -v` is empty), so no pipeline file is committed. When
one exists, the job is the same three commands with the token in a secret named `SONAR_TOKEN`, plus
`sonar.qualitygate.wait=true`. On GitHub that is `SonarSource/sonarqube-scan-action`; on Gitea or
GitLab it is a shell step, since the scanner is a plain dotnet tool.

The Ci secret name and the local variable differ on purpose: `SONARQUBE_TOKEN` is what this project
reads locally, and `SONAR_TOKEN` is the convention CI providers document.
