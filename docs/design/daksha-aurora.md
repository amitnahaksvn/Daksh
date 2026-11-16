# Daksha Aurora appearance system

Daksha supports three visual styles: Bento, Neural and Aurora. The selected style and accent are independent preferences and are stored locally on each client.

## Accent colours

The web appearance controls and Flutter appearance menu provide five presets plus a custom HEX colour. Custom colours must be six-digit RGB HEX values in the form `#RRGGBB`. The preference is persisted under `daksh.accent` (web local storage) or `daksh.accentColor` (Flutter shared preferences).

Web tokens use `--palette` for the active accent. The same value drives primary actions, focus indicators and the Daksha presence orb. Flutter parses the stored HEX value into the Material `ColorScheme.primary`.

## Theme styles

- **Bento**: supports Day, Night and Auto.
- **Neural**: dark-first AI visual style.
- **Aurora**: Daksha's midnight navy and violet identity.

## Branding

Use the reusable `DakshaBrand` component and the SVG assets under `apps/web/public` for web brand placements. Do not replace the lotus with an emoji or a generic icon.

## Adding a screen

Prefer existing shared UI components and semantic theme tokens. Avoid hard-coded colours. For web, use `bg-canvas`, `bg-surface`, `bg-surface-2`, `text-text`, `text-muted`, `border-line` and `bg-accent`. For Flutter, use `Theme.of(context).colorScheme`, `textTheme`, `cardTheme` and `inputDecorationTheme`.

## Accessibility

Keep text contrast readable, preserve visible keyboard focus, and do not use accent colour as the only signal for status or validation. Check custom colours against both light and dark surfaces before using them for small text.
