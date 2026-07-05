# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project intends to adhere to
[Semantic Versioning](https://semver.org/) once it ships its first NuGet release.

## [Unreleased]

### Added

- Solution scaffolding: all `src/`, `tests/`, and `examples/` project skeletons with the
  dependency-direction wiring described in `specs/001-inline-keyboard-hooks/plan.md`.
- Repo hygiene: `Directory.Build.props`, `Directory.Packages.props` (central package management),
  `.editorconfig`, `LICENSE` (MIT), CI workflow.
- Transport-agnostic F# core (`TgLLM.Core`): domain model, value objects (`ButtonLabel`,
  `MessageText`), the `CallbackToken` codec, `Keyboard`/`KeyboardPlan` pure kernel, `Routing`
  decision logic, ports (`IUpdateSource`, `IHookStore`, `IPressDispatcher`, `IBotApiClient`,
  `IHookObserver`), the default `InMemoryHookStore` and `PerChatChannelDispatcher`
  implementations, and the ack-first `UpdateProcessor` engine — covered by Expecto + FsCheck
  property tests.
