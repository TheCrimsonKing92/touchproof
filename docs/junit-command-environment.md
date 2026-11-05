# Explicit inputs for a JUnit command

Use `environment` to opt one declared command into fixture-backed cases. Tool discovery checks
prerequisites; it does not choose fixtures. For example, a project whose `go test` command emits
JUnit through its reporting wrapper could declare:

```json
{
  "version": 1,
  "environmentContract": {
    "version": 1,
    "tools": [
      { "name": "go", "command": "go", "arguments": ["version"] },
      { "name": "Maven", "command": "mvn", "arguments": ["--version"] },
      { "name": "Gradle", "command": "gradle", "arguments": ["--version"] }
    ]
  },
  "checks": [
    {
      "name": "fixture integration tests",
      "type": "junit-command",
      "command": "pwsh",
      "arguments": ["-NoProfile", "-File", "scripts/run-integration-junit.ps1"],
      "project": "integration",
      "reports": ["reports/*.xml"],
      "environment": {
        "CROSSWALK_MAVEN_PROJECT": "fixtures/maven-project",
        "CROSSWALK_GRADLE_PROJECT": "fixtures/gradle-project"
      }
    }
  ]
}
```

The example assumes the project supplies the reporting script and fixtures. Paths in the values
remain relative to the command's `integration` working directory. Values are literal strings:
no variable expansion, shell interpolation, trimming, path rewriting or external value lookup occurs.
An empty string is a value, not an instruction to unset a variable.

Omitting the map and supplying `{}` both mean no overrides. Explicit `null`, non-object maps,
non-string values, empty names, `=` in names, embedded NULs and duplicate names are rejected.
Names compare case-insensitively on Windows and case-sensitively on Unix. Harness-owned variables,
provider homes and declared tool homes are protected. Other check kinds reject the field, even `{}`.

Inputs reach only this command's child environment after the normal scrub and ownership setup.
The parent, other checks, report parser and discovery probes receive no overrides. Changing an
input changes effective plan identity and invalidates reusable evidence. Inputs do not change
deadlines, cancellation, fresh-report checks or missing-contracted-tool skip rejection.

For checks with inputs, diagnostics retain failure categories and process counters while suppressing
command/report excerpts and links to raw stdout/stderr captures. Original reports and normalized
test evidence retain their contents for parsing and gate decisions; treat those artifacts accordingly.

This is a documentation example. Live Maven/Gradle fixture activation is a later operator rollout.
