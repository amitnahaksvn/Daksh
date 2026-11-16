# Daksha Aurora design system

## Brand assets
- `apps/web/public/daksha-lotus.svg`: transparent lotus icon for compact use and favicon.
- `apps/web/public/daksha-brand.svg`: full lotus + Daksha + The Prajapati lockup.
- Android launcher uses `apps/mobile/android/app/src/main/res/drawable/daksha_launcher.xml`.
- iOS AppIcon catalog still requires generated PNG renditions; replace the existing AppIcon PNG set from the lotus SVG before App Store submission.

## Web theme
- Theme preferences are managed in `apps/web/src/lib/theme.ts`.
- CSS design tokens and style variants live in `apps/web/src/app/globals.css`.
- `AppearanceControls` exposes Bento, Neural and Aurora plus Day/Night/Auto where supported.
- Accent presets are Violet, Ocean, Emerald, Rose and Amber. The Custom control accepts any six-digit HEX colour and persists it in local storage.
- Use semantic tokens such as `bg-canvas`, `bg-surface`, `bg-surface-2`, `text-text`, `text-muted`, `border-line`, `bg-accent`; do not hardcode theme colours in screens.

## Flutter theme
- `apps/mobile/lib/core/theme.dart` centralizes Material 3 colours, surfaces, cards, fields, buttons and navigation.
- `apps/mobile/lib/core/theme_mode.dart` persists style, brightness and accent in SharedPreferences.
- Accent values accept the five presets or a `#RRGGBB` value.
- Add new screen styling through Theme.of(context).colorScheme and Theme.of(context).textTheme.

## Design tokens
Aurora: primary `#8B5CF6`, primary dark `#6D28D9`, accent `#A855F7`, background `#0B1020`, surface `#0F1220`, card `#1A1F32`, border `#2A2F45`, text `#F8FAFC`, muted `#94A3B8`, success `#22C55E`, warning `#F59E0B`, danger `#EF4444`.

Typography uses the system sans stack with a clean modern fallback. For a future bundled Inter/Manrope rollout, add licensed font files to both apps and declare them in the platform font configuration.
