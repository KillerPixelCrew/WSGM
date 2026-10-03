### Requirements already supplied

1. Plan a full, substantive WSGM refactor before the intended release.
2. Do not restrict the proposal to superficial cleanup merely to avoid difficult changes.
3. The maintainer expects extensive retesting for 2.1.0 and is willing to use this period for the refactor.
4. Use the full dispatch interview, source-grounded planning, mandatory plan review, and explicit execution-approval workflow.
5. Confirmed scope: all first-party WSGM projects, plus needed changes to the reusable toolkits/libraries. Human answer: "All WSGM projects + needed toolkit/library changes".
6. Fully audit both reusable libraries: `external/windows-device-control` and `external/steam-ui-toolkit`. Cover each library as a whole, including architecture, resource/lifecycle ownership, state and concurrency, public contracts, error handling, test quality, and supporting assets/docs/build boundaries. The audit is not restricted to APIs WSGM currently calls. Findings must inform the proposed refactor and its task graph.

7. Accepted end-state requirements: clear state/resource ownership and explicit dependencies; components testable without live Steam, hardware, or the real user configuration; behavioral coverage of startup, partial failure, and shutdown; reusable libraries containing no WSGM-specific policy.
8. Extend the audit across the full approved scope to identify simplification opportunities, mechanisms that should be reusable, duplication and unclear library boundaries. Each finding must have a disposition in the refactor proposal; concrete extractions and boundary design remain the planner's responsibility.

9. Preserve the current UI appearance and user-visible workflows. Backend/internal implementation and boundaries may be changed deeply. Human answer: "We can basically change everything behind the Scenes. I am quite happy with how the UI looks and works right now." No feature removal or UI/workflow redesign is authorized by this answer.

10. Breaking changes to public Device/Plugin SDK APIs, plugin/package contracts, and both reusable-library APIs are allowed. Update all in-repo consumers coherently. Existing external consumer compatibility is not a required constraint. Human answer: "Allow breaking changes; update all in-repo consumers".

11. Automatically migrate current user settings, profiles and plugin configuration to the refactored app, and preserve Windows recovery state. A clean configuration reset is not the accepted approach. Human answer: "Migrate current settings and profiles; preserve recovery state".

12. Keep the Avalonia framework/dependency and VIIPER fixed, including their dependency source/versions and the VIIPER gitlink. WSGM-owned UI internals (view models, bindings, service wiring and backdrop integration) may be refactored while preserving appearance and behavior. Human clarification: "Keep Avalonia fixed; WSGM UI internals may be refactored". No other technical foundation exclusions were supplied.

13. Explicit exception to repository manual-first timing for this refactor: agents may run isolated automated tests and coverage during planning to establish the baseline, and targeted automated tests throughout implementation before the maintainer's manual retest. Human answer: "Yes: automated baseline and targeted tests throughout". Existing test isolation and live-state safety constraints still apply; automated evidence does not replace required live/manual acceptance.

14. The release gated by the refactor is 2.1.0. 2.0 names the evolution from 1.0 and has already been released. Human clarification: "2.0 is the general evolution form 1.0. 2.0 was released already. 2.1.0 is the next." No deadline or effort/usage budget was supplied in this answer.

15. Manual acceptance environments: the current notebook with Intel/NVIDIA Optimus; a desktop using the IR Monitor Switch; MSI Claw 8 A2VM; and a dedicated tester with the Xbox Ally X. The desktop GPU and exact OS/Steam versions were not specified. Do not replace Xbox Ally X with the earlier ROG Ally X model or claim fresh hardware validation from older notes. This identifies available testing; it does not authorize messaging the tester or running live hardware/deployment actions.

16. Require the complete 2.1.0 acceptance gate: all agreed refactor work complete and audit findings resolved or explicitly dispositioned; passing required build/behavioral tests; independent validation of Windows Device Control and Steam UI Toolkit; full manual regression matrix on the available setups, covering recovery, controller/device/power, Steam UI, IR switching and install/update/configuration migration. Human answer: "Require that complete gate, including the manual regression matrix". Explicit finding dispositions do not excuse unfinished agreed implementation or silently skipped validation.

17. Explicit pre-planning delegation: before continuing, run a full Claude Opus 5.5 multi-agent code review with the Reddit criticism and agreed scope. Report every finding, no matter how nitpicky. This authorizes the isolated review and Claude's reviewer delegation before interview completion; it does not approve refactor planning/implementation or waive mandatory plan review. Keep all raw findings and exhaustive file coverage.

