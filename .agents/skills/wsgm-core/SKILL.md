---
name: wsgm-core
description:
  Implement, review, or diagnose cross-cutting WSGM application work in Program, Core, Shell,
  Settings, Overlay, Input, or Interop, including process modes, configuration, recovery, session
  ownership, UI-thread state, manager lifetimes, and shutdown. Use for device-independent WSGM work
  or changes spanning application layers; use the specialized Steam CEF, Device SDK, or Device Lab
  skills for those domains.
---

# WSGM Core

Change the resident Windows shell application through its existing owners and recovery boundaries.
Do not turn `ShellSession`, `App.axaml.cs`, or this skill into a catch-all.

For the general repository structure and where a new task belongs, start with the `wsgm` skill. Use
this skill for the application state, ownership and lifetime work after that routing decision.

## Establish current truth

1. Resolve the root with `git rev-parse --show-toplevel`, then read the root and nearest
   `AGENTS.md`. Inspect `git status --short --branch` and `git submodule status --recursive` before
   editing.
2. Start at `docs/README.md`, then read `docs/architecture.md`, `docs/development.md`,
   `docs/decisions.md` and the mechanism document for the subsystem. Use `docs/source-map.md` to
   locate source owners. Current code/tests beat a dated plan or hardware note.
3. Read [references/architecture-and-routing.md](references/architecture-and-routing.md) to select
   the owner and specialized skill.
4. Read [references/state-lifetime-and-safety.md](references/state-lifetime-and-safety.md) before
   changing startup, configuration, long-lived services, shell transitions, or teardown.
5. Read [references/change-and-verification.md](references/change-and-verification.md) before
   implementation and delivery.

The user's task controls scope. Inspection or diagnosis does not authorize shell/boot mode, package
maintenance, Device Lab hardware work, live Steam mutation, SD-card formatting, installation, or a
release build.

## Route policy and mechanism correctly

- `Program.cs` owns explicit modes and pre-UI one-shots. Preserve their deliberate precedence.
- `Core` owns nonvisual policy, strict configuration, recovery primitives, package policy, and
  integration-independent services.
- `Shell` owns the live session, manager composition, session transitions, reconciliation, and
  deterministic teardown.
- Settings/Overlay views report user intent and project state. Their owning windows/controllers may
  hold a bounded surface lifetime: `SettingsWindow` has a focused Steam Input lease, and
  `OverlayController` owns overlay capture, focus, cursor, and lease transitions. Feature controls
  still do not acquire unrelated hardware, Steam CEF, RTSS, or shell resources.
- `Input` owns canonical input and virtual-target encoding/routing. `Interop` owns native ABI calls.
- `Controls` and `Themes` own presentation primitives; `App.axaml.cs` only composes application/UI
  lifetime.
- SDK, Device Lab, and device-package behavior belongs in its own src project, with changes
  delivered together in WSGM. Reusable Steam, Windows-device-control, and Steam Input behavior
  belongs in the corresponding submodule.

For Steam CEF work use `wsgm-steam-cef-toolkit` or `wsgm-steam-cef-debugging`. For the semantic
device contract/host use `wsgm-device-sdk`; for hardware discovery use `wsgm-device-lab`.

Before any live CEF tool connection, inspect Steam's file logs and establish that Steam and Big
Picture have fully started. A listening port, process, window or target list is insufficient. Early
attachment can hang the entire Steam UI and require force-closing Steam. Follow the specialized
skills' readiness procedure; do not connect in order to discover whether startup is finished.

## Preserve the resident-process invariants

- Keep restore-shell and other recovery-critical entry points usable before logging, configuration,
  Avalonia, GPU, package discovery, and normal service construction as their current order requires.
- Use one explicit owner for every long-lived service. Start and dispose it in a documented order;
  cancellation closes admission before dependencies disappear, and one cleanup failure must not skip
  the rest. Distinguish session-long managers from intentionally per-mode resources: both still need
  exactly one live owner and a reference that cannot be overwritten while the old instance survives.
- Runtime config reload replaces `AppConfig`. Do not retain references into the old object. Strict
  mutations abort on unreadable state instead of overwriting recovery snapshots with defaults.
- Marshal UI-observable state through the Avalonia dispatcher. Keep blocking, hardware, network,
  process, and cold-acquisition work off the UI thread. Coalesce worker refreshes, publish detached
  snapshots only to the still-current owner, and reconcile bound collections in place when focus or
  item identity matters.
- Treat Device Integration off as a real architecture mode: the session coordinator/owner marker
  still exist, but there is no Device plugin lifecycle, controller target, Device hardware write, or
  AutoTDP; independent WSGM, explicitly enabled common plugins and RTSS behavior remains usable.
- Serialize capability or other persistent writes. Preserve each adapter's accepted-write contract:
  publish an accepted command as observed where the Windows/device API owns success, without a
  mandatory readback or control gate. On uncertainty, use a separate observation or a new explicit
  user action; never automatically retry a write that might already have succeeded. Explorer
  recovery and ownership-ledger cleanup retain their specific verification rules.
- Avoid allocation and logging at controller, sensor, frametime, or telemetry cadence. Log decisions
  and transitions with stable change keys.
- Fail open to a usable Windows desktop. Preserve exact ownership of Explorer, tray, input, Steam,
  device, display, and temporary state; remove only WSGM's own change.

## Implement as a state flow

Trace the event from input to the one state owner, then to each projection and side effect. Extend
an existing manager/coordinator and its state transition before adding a new service. Keep durable
user policy in `ConfigStore`, transient process state in its manager, UI-only state in the view
model, and native handles in an owned disposable service.

Keep classes and functions small enough to read as one responsibility. Reuse the existing bounded
I/O, launch, shutdown, control and theme components listed in the architecture reference before
adding a helper. Introduce an abstraction only for a concrete shared need; do not add a generic
framework, a second policy engine or another copy of the same UI workflow.

A new Steam/CEF feature must also provide the same feature and workflow in the WSGM Overlay. Both
surfaces use the same state owner, commands and availability rules. Follow the specialized CEF skill
for Steam-native components and layout; presentation may differ, but behavior must stay aligned.

For behavior changes, cover success, refusal, cancellation, repeated invocation, partial startup,
config replacement and teardown in focused tests. For a cross-layer feature, test the policy
independently from the Windows/Steam/hardware adapter. Documentation-only corrections need source,
link and formatting checks, not new tests that mirror prose.

## Finish with repository evidence

Follow the root AGENTS.md manual-first timing: build and deliver a requested development deployment
before running test suites. After the maintainer reports manual testing, run the relevant tests and
gate. Reconcile affected docs/plans/guidance. Do not use `-Fix` in a dirty tree unless the task
includes reviewing all formatting changes. `build.ps1` is for an explicitly requested setup/release
handoff, not ordinary verification.
