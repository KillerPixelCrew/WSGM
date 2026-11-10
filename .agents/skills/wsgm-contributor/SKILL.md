---
name: wsgm-contributor
description:
  Apply WSGM code conventions and review contributions end to end. Use for implementation, cleanup
  and PR review alongside the skill for the code owner; includes coupled repository delivery and
  honest validation scope.
---

# WSGM contributions

Read the repository's AGENTS.md first. Pair this skill with `wsgm` and its owning specialist; this
skill supplies contribution conventions, not device or Steam protocol knowledge.

## Code convention and style

C# uses expanded braces, `var` locals, no trailing commas in multiline lists and named arguments on
literals. With `var`, spell the construction type when inference would lose it, such as
`var frames = new List<byte[]>();`. Use target-typed `new` where the declaration makes the type
evident. Public contracts include XML documentation for outcomes, cancellation and ownership.
`.editorconfig` overrides defaults; `WSGM.slnx.DotSettings` supplies Rider's profile. Rider Full
Cleanup is the C# authority. Match nearby code for small edits; ordinary work does not authorize
whole-project cleanup. Format changed Prettier-owned files before commit, not the entire repository.

Keep one top-level type per file in WSGM Core/Shell, in the owning feature folder. Prefer named
locals for multiline projection and validation expressions before constructing a state record.
Remove pass-through wrappers when the existing owner can be called directly. Extract shared policy
only when it actually repeats; do not add a generic framework to eliminate a few simple expressions.
Preserve explanatory protocol and failure comments. Write concise prose without em dashes.

## Review the behavior, then the diff

Record the exact PR heads, bases and dependency gitlinks. Read the PR description, current guidance,
all changed files and relevant surrounding callers, consumers and tests. A style pass is not an
end-to-end review. State a limited scope explicitly if the request only asks for conventions.

Trace user intent through admission, one state owner, native dispatch, result publication,
persistence, both UI surfaces and teardown. Include first acquisition, unknown readings, repeated
commands, partial dispatch, cancellation, path switches, disable, suspend/resume and process restart
where those transitions exist. Compare requested values with published values and downstream preset
or assignment matching. A renamed control must still perform the operation its label promises on
every available backend. A test of an encoder alone cannot prove lifecycle or host behavior.

Give each finding a concrete trigger, changed file/line, consequence and correction. Distinguish a
reproducible source defect from a product choice, known merge prerequisite or missing acceptance. Do
not prescribe a speculative rewrite as a correctness fix. Check an independent review against the
source before forwarding it; independent agents are useful when requested, not a required handoff
for ordinary contributions. Never imply that two agreeing reviewers proved runtime behavior.

## Coupled delivery and proof

Check that a clean recursive checkout contains the dependency that produces the consumed contract. A
dependency PR merging does not update WSGM's gitlink. An explicitly sequenced dependency bump is a
delivery prerequisite, not automatically a compilation defect. Publish child commits before
recording parent pins; preserve unrelated changes and follow the existing branch instructions.

Use AGENTS.md's validation budget. Guidance-only edits need formatting, skill/link validation and
`eng/check-agent-guidance.ps1`, not compilation, UI rendering or a full gate. For code changes,
report actual checks and distinguish fixtures, rendered layouts, live Steam and attended hardware.
Never run a full gate solely because there is a PR, a push or an AI-generated change.
