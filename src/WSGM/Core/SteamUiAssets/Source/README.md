# Steam UI bootstrap source

The build concatenates these source fragments in the explicit order recorded by
`eng/build-steam-assets.mjs`, type-checks the combined program, strips TypeScript annotations, and
formats one reviewable injected asset. The fragments deliberately share one lexical scope: Steam's
gates close over the private bridge functions and must not publish a second runtime API merely to
cross a source-file boundary.

- `bridge.ts` owns the request protocol, subscriptions, generations, ownership markers, and the
  process-level namespace.
- `gates/` contains the independently reversible Steam service/store integrations.
- `components.ts` owns the React component registry, control rows, placement table, and teardown.
- `types.ts` contains compile-time-only declarations.

Adding a fragment requires adding it to the ordered `sourcePaths` list. The asset check rebuilds the
same combined program and rejects stale generated JavaScript or hashes.
