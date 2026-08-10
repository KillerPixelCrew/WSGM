# Themes

Themes define the visual token system and control themes; `App.axaml` should remain includes-only.

- Use palette resources for every colour. Use `DynamicResource` for the runtime-replaceable accent
  family and `StaticResource` for stable tokens.
- Keep the single focus treatment: `FocusAdorner={x:Null}` plus a constant 2 px border that changes
  to the accent when focused.
- Put reusable visual behavior in the appropriate shared theme or `Controls\` component rather than
  copying styles into page/overlay XAML.
