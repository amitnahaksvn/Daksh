import 'package:flutter/material.dart';

/// Daksh's visual language: the Bento grid look. A soft grey canvas, white rounded tiles, lime and pink accent colours
/// and near-black ink. At night the tiles are charcoal and the lime becomes the main accent. Every screen reads this
/// theme, so changing it restyles the app.
class AppTheme {
  AppTheme._();

  static const Color ink = Color(0xFF17181C);
  static const Color lime = Color(0xFFC8F169);
  static const Color pink = Color(0xFFFFC9D6);
  static const double tileRadius = 22;

  static ThemeData light() => _build(Brightness.light);
  static ThemeData dark() => _build(Brightness.dark);

  static ThemeData _build(Brightness brightness) {
    final isDark = brightness == Brightness.dark;
    final canvas = isDark ? const Color(0xFF111214) : const Color(0xFFECEEF2);
    final surface = isDark ? const Color(0xFF1B1C20) : Colors.white;
    final surface2 = isDark ? const Color(0xFF26282D) : const Color(0xFFF4F5F7);
    final text = isDark ? const Color(0xFFF2F3F5) : ink;
    final muted = isDark ? const Color(0xFF9A9EA8) : const Color(0xFF5A5D66);
    final border = isDark ? const Color(0x17FFFFFF) : const Color(0x1417181C);
    final primary = isDark ? lime : ink;
    final onPrimary = isDark ? ink : Colors.white;

    final scheme = ColorScheme(
      brightness: brightness,
      primary: primary,
      onPrimary: onPrimary,
      primaryContainer: isDark ? const Color(0xFF2A3315) : lime,
      onPrimaryContainer: isDark ? lime : ink,
      secondary: pink,
      onSecondary: ink,
      secondaryContainer: isDark ? const Color(0xFF3A2430) : pink,
      onSecondaryContainer: isDark ? pink : ink,
      tertiary: lime,
      onTertiary: ink,
      error: isDark ? const Color(0xFFFFB4AB) : const Color(0xFFB3261E),
      onError: isDark ? ink : Colors.white,
      errorContainer: isDark ? const Color(0xFF3A1D1D) : const Color(0xFFFFDAD6),
      onErrorContainer: isDark ? const Color(0xFFFFB4AB) : const Color(0xFF7A1F1F),
      surface: surface,
      onSurface: text,
      onSurfaceVariant: muted,
      outline: isDark ? const Color(0xFF3A3D44) : const Color(0xFFCFD3DA),
      outlineVariant: border,
      surfaceContainerLowest: isDark ? const Color(0xFF111214) : Colors.white,
      surfaceContainerLow: surface,
      surfaceContainer: surface2,
      surfaceContainerHigh: surface2,
      surfaceContainerHighest: isDark ? const Color(0xFF30333A) : const Color(0xFFEAECF0),
      inverseSurface: isDark ? const Color(0xFFF2F3F5) : ink,
      onInverseSurface: isDark ? ink : Colors.white,
      inversePrimary: isDark ? ink : lime,
      surfaceTint: Colors.transparent,
    );

    final pill = RoundedRectangleBorder(borderRadius: BorderRadius.circular(999));
    final tile = RoundedRectangleBorder(borderRadius: BorderRadius.circular(tileRadius), side: BorderSide(color: border));
    OutlineInputBorder field(Color c, [double w = 1]) =>
        OutlineInputBorder(borderRadius: BorderRadius.circular(18), borderSide: BorderSide(color: c, width: w));

    return ThemeData(
      useMaterial3: true,
      brightness: brightness,
      colorScheme: scheme,
      scaffoldBackgroundColor: canvas,
      appBarTheme: AppBarTheme(
        backgroundColor: canvas,
        foregroundColor: text,
        elevation: 0,
        scrolledUnderElevation: 0,
        surfaceTintColor: Colors.transparent,
        titleTextStyle: TextStyle(color: text, fontSize: 22, fontWeight: FontWeight.w800, letterSpacing: -0.4),
      ),
      cardTheme: CardThemeData(color: surface, elevation: 0, margin: EdgeInsets.zero, shape: tile),
      listTileTheme: ListTileThemeData(iconColor: muted, shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16))),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: surface,
        border: field(border),
        enabledBorder: field(border),
        focusedBorder: field(primary, 1.6),
        errorBorder: field(scheme.error),
        contentPadding: const EdgeInsets.symmetric(horizontal: 18, vertical: 15),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: primary,
          foregroundColor: onPrimary,
          padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 16),
          shape: pill,
          textStyle: const TextStyle(fontWeight: FontWeight.w700, fontSize: 15),
        ),
      ),
      textButtonTheme: TextButtonThemeData(style: TextButton.styleFrom(foregroundColor: text, shape: pill, textStyle: const TextStyle(fontWeight: FontWeight.w700))),
      navigationBarTheme: NavigationBarThemeData(
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        height: 68,
        indicatorColor: scheme.primaryContainer,
        indicatorShape: pill,
        labelBehavior: NavigationDestinationLabelBehavior.alwaysShow,
        iconTheme: WidgetStateProperty.resolveWith((s) => IconThemeData(color: s.contains(WidgetState.selected) ? scheme.onPrimaryContainer : muted)),
        labelTextStyle: WidgetStateProperty.resolveWith((s) => TextStyle(
              fontSize: 12,
              fontWeight: s.contains(WidgetState.selected) ? FontWeight.w800 : FontWeight.w600,
              color: s.contains(WidgetState.selected) ? text : muted,
            )),
      ),
      floatingActionButtonTheme: const FloatingActionButtonThemeData(backgroundColor: pink, foregroundColor: ink, elevation: 0, highlightElevation: 0),
      dialogTheme: DialogThemeData(backgroundColor: surface, surfaceTintColor: Colors.transparent, shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(26))),
      snackBarTheme: SnackBarThemeData(
        behavior: SnackBarBehavior.floating,
        backgroundColor: isDark ? const Color(0xFFF2F3F5) : ink,
        contentTextStyle: TextStyle(color: isDark ? ink : Colors.white, fontWeight: FontWeight.w600),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      ),
      popupMenuTheme: PopupMenuThemeData(
        color: surface,
        surfaceTintColor: Colors.transparent,
        elevation: 6,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18), side: BorderSide(color: border)),
        textStyle: TextStyle(color: text, fontWeight: FontWeight.w600),
      ),
      switchTheme: SwitchThemeData(
        thumbColor: WidgetStateProperty.resolveWith((s) => s.contains(WidgetState.selected) ? onPrimary : muted),
        trackColor: WidgetStateProperty.resolveWith((s) => s.contains(WidgetState.selected) ? primary : surface2),
        trackOutlineColor: WidgetStatePropertyAll(border),
      ),
      progressIndicatorTheme: ProgressIndicatorThemeData(color: primary),
      dividerTheme: DividerThemeData(color: border, space: 1),
    );
  }
}
