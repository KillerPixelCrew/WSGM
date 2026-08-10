# Settings

Settings is the safe local configuration UI. Its pages are kept alive and switched by visibility so
scroll position and short-lived editing state survive tab changes.

- Persist through `ConfigStore.SaveMerged` and the splash-assets transaction; never save directly or
  promote image sidecars before the config save succeeds.
- Tests must use the internal view-model constructor with an explicit `AppConfig` and temporary asset
  directories. Never invoke parameterless `SettingsViewModel` or real `ConfigStore.Load/Save`.
- Maintain the layout floor: Settings minimum 1024×640; a page that needs scrolling earns another tab.
- Shortcut recording owns its hook only while recording and must dispose it on every close/cancel path.
