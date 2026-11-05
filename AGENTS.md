# Agent notes

## SonarQube

The project is analyzed by the self-hosted SonarQube Server at https://sonarqube.verduguez.com,
project `overlyric`. [docs/sonarqube.md](docs/sonarqube.md) has the setup, the scope and the known
limits.

The MCP tools come through the Docker MCP Toolkit gateway and are named `mcp__docker__*`:

- Before changing a file, read its open issues (`search_sonar_issues_in_projects`) and the gate
  (`get_project_quality_gate_status`) so a change does not add to a known problem.
- After changing a file, run `analyze_code_snippet` on it instead of guessing.
- Report issues with severity and rule key; fix BLOCKER and CRITICAL first. Never mark an issue
  accepted or false-positive without asking.

A full analysis is the standard three commands — `dotnet-sonarscanner begin /k:overlyric /v:<version>
"/s:<repo>\SonarQube.Analysis.xml" /d:sonar.token=$env:SONARQUBE_TOKEN`, `dotnet build
src\Overlyric.csproj`, `dotnet-sonarscanner end /d:sonar.token=$env:SONARQUBE_TOKEN`.
There is no `sonar-project.properties`: the .NET scanner ignores it. Under the default
`workspace-write` file policy the sandbox blocks `bin`/`obj` writes and .NET TLS, so a scan has to
run from a normal terminal; `danger-full-access` runs it from a session unchanged.
