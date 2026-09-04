# Themes

Themes owns shared Avalonia resources and control presentation.

- App.axaml includes theme resources; it does not become a second theme file.
- Put colors, brushes, typography, spacing, radii, sizing, and focus treatment behind shared
  semantic tokens.
- Use DynamicResource for user- or system-changing accent resources. Use StaticResource for stable
  application tokens.
- Keep one visible 2-pixel focus border. Disable the framework focus adorner where the themed
  control supplies that border so focus is not drawn twice.
- Prefer selectors and control themes over per-page copies. A feature-specific token must have a
  semantic name and a clear owner.
- Verify default, hover, pressed, disabled, selected, focused, high-contrast, and accent-changing
  states for affected controls.

Keep visual policy here and behavioral policy in the owning control or feature. Run focused UI tests
where available and inspect every changed state at the supported minimum window size.
