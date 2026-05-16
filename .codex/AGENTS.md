# Chobo Repository Notes

We're working on **Chobo**, a product designed to orchestrate ClickHouse backups and restores. 

Although ClickHouse natively supports backing up and restoring data, it lacks some features such as state management (of all the existing backups), scheduling, etc. 

### Overview
The product has 3 main parts: ChoboServer, ChoboCli (CLI used to execute commands and interact with ChoboServer)

#### ChoboServer
* ChoboServer stores internal data in Sqlite DB. Handling upgrades, and have a clear upgrade-path to new version is a mandatory requirement.

#### ChoboCli
* The CLI is friendly utility to interact with ChoboServer
* Commands are in form of ChoboCli <subject> <verbs> <args>. For example: `ChoboCli scheduling show --targetCluster <ClusterName>` or `ChoboCli backups list --targetCluster <ClusterName`

#### ChoboTests
* A unit-tests projects we'll use for unit-tests (not full system tests, described later) using dotnet test

#### ChoboWeb
* Web UI interface

## Tech stack
* Everything is in .NET (C#), latest version.
* For logging, use Serilog configured from `appsettings.json`.
* In product project DI, use `Serilog.ILogger`, not Microsoft `ILogger<T>`.
* Metrics use `System.Diagnostics.Metrics` and OpenTelemetry Prometheus exporter.

## System-Testing mechanism (TestingSuite folder)
* Tests are based on a Docker Compose files that launch ClickHouse server in different shapes, backup destination and backup server and the CLI
* The tests use mostly the CLI and not executing direct actions with the API.
* Each test has a setup phase, action phase (executing backups & restores) and verification phase (ensuring the desired state post-upgrade)
* Tests are based on PowerShell scripts, with a shared infra (so code won't be repeated) and 'TestManager.ps1' file used to execute the desired tests

## Current Project Structure
- Each product project has its own `AGENTS.md` with local guidance.

## Working agreements
- DO NOT WRITE COMMENTS UNLESS REALLY REQUIRED. Comments are only for the most complex code exists.
- Ask for confirmation before adding new production dependencies.
- Every skill created to support the project, and every utility script or asset must be inside the solution dir, and not locally on the developer's computer
- Prefer lock-free/concurrent collections in hot-path code.
- Private fields should use `_camelCase`.
- Run `dotnet build Upendi.sln -v minimal`, `.\scripts\Test-DirectUdp.ps1`, and `.\scripts\Test-UpendiPath.ps1` after product-path changes.
- The CLI should be COMPLETE. Meaning, if you add feature to a controller or web ui, it should also be added to the CLI
- Any backend changes (in ChoboServer) requires running unit-tests (dotnet test) and relevant system tests from the system test suite (see relevant skill)