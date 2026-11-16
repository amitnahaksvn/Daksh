import 'package:flutter/material.dart';

/// Central Material 3 theme for Daksha. Screens should consume Theme.of(context).
class AppTheme {
  AppTheme._();

  static const Color ink = Color(0xFF17181C);
  static const Color lime = Color(0xFFC8F169);
  static const Color pink = Color(0xFFFFC9D6);
  static const double tileRadius = 22;

  static Color accent(String name) {
    if (RegExp(r'^#[0-9a-fA-F]{6}$').hasMatch(name)) {
      return Color(int.parse(name.substring(1), radix: 16) | 0xFF000000);
    }
    return switch (name) {
      'blue' => const Color(0xFF3B82F6),
      'emerald' => const Color(0xFF10B981),
      'rose' => const Color(0xFFF43F5E),
      'amber' => const Color(0xFFF59E0B),
      _ => const Color(0xFF8B5CF6),
    };
  }

  static ThemeData light({String style = 'bento', String palette = 'violet'}) =>
      _build(Brightness.light, style, accent(palette));
  static ThemeData dark({String style = 'bento', String palette = 'violet'}) =>
      _build(Brightness.dark, style, accent(palette));

  static ThemeData _build(Brightness brightness, String style, Color accentColor) {
    final isDark = brightness == Brightness.dark;
    final aurora = style == 'aurora';
    final neural = style == 'neural';
    final canvas = neural
        ? const Color(0xFF06070F)
        : aurora
            ? const Color(0xFF0B1020)
            : isDark
                ? const Color(0xFF111214)
                : const Color(0xFFECEEF2);
    final surface = neural
        ? const Color(0xFF101224)
        : aurora
            ? const Color(0xFF0F1220)
            : isDark
                ? const Color(0xFF1B1C20)
                : Colors.white;
    final surface2 = neural
        ? const Color(0xFF202238)
        : aurora
            ? const Color(0xFF1A1F32)
            : isDark
                ? const Color(0xFF26282D)
                : const Color(0xFFF4F5F7);
    final text = isDark || aurora || neural ? const Color(0xFFF8FAFC) : ink;
    final muted = isDark || aurora || neural ? const Color(0xFF94A3B8) : const Color(0xFF5A5D66);
    final border = isDark || aurora || neural ? const Color(0xFF2A2F45) : const Color(0x1417181C);
    final scheme = ColorScheme(
      brightness: brightness,
      primary: accentColor,
      onPrimary: Colors.white,
      primaryContainer: accentColor.withValues(alpha: 0.18),
      onPrimaryContainer: text,
      secondary: accentColor.withValues(alpha: 0.72),
      onSecondary: Colors.white,
      secondaryContainer: accentColor.withValues(alpha: 0.16),
      onSecondaryContainer: text,
      tertiary: const Color(0xFF22C55E),
      onTertiary: ink,
      error: const Color(0xFFEF4444),
      onError: Colors.white,
      errorContainer: const Color(0xFF3A1D1D),
      onErrorContainer: const Color(0xFFFFB4AB),
      surface: surface,
      onSurface: text,
      onSurfaceVariant: muted,
      outline: border,
      outlineVariant: border,
      surfaceContainerLowest: canvas,
      surfaceContainerLow: surface,
      surfaceContainer: surface2,
      surfaceContainerHigh: surface2,
      surfaceContainerHighest: surface2,
      inverseSurface: text,
      onInverseSurface: ink,
      inversePrimary: accentColor,
      surfaceTint: Colors.transparent,
    );
    final pill = RoundedRectangleBorder(borderRadius: BorderRadius.circular(999));
    final tile = RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(tileRadius),
      side: BorderSide(color: border),
    );
    OutlineInputBorder field(Color color, [double width = 1]) =>
        OutlineInputBorder(
          borderRadius: BorderRadius.circular(14),
          borderSide: BorderSide(color: color, width: width),
        );

    return ThemeData(
      useMaterial3: true,
      brightness: brightness,
      colorScheme: scheme,
      scaffoldBackgroundColor: canvas,
      fontFamily: 'Inter',
      appBarTheme: AppBarTheme(
        backgroundColor: canvas,
        foregroundColor: text,
        elevation: 0,
        scrolledUnderElevation: 0,
        surfaceTintColor: Colors.transparent,
        titleTextStyle: TextStyle(
          color: text,
          fontSize: 20,
          fontWeight: FontWeight.w800,
          letterSpacing: -0.4,
        ),
      ),
      cardTheme: CardThemeData(
        color: surface,
        elevation: 0,
        margin: EdgeInsets.zero,
        shape: tile,
      ),
      listTileTheme: ListTileThemeData(
        iconColor: muted,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: surface,
        border: field(border),
        enabledBorder: field(border),
        focusedBorder: field(accentColor, 1.6),
        errorBorder: field(scheme.error),
        contentPadding: const EdgeInsets.symmetric(horizontal: 18, vertical: 15),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: accentColor,
          foregroundColor: Colors.white,
          padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 16),
          shape: pill,
          textStyle: const TextStyle(fontWeight: FontWeight.w700, fontSize: 15),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(foregroundColor: text, shape: pill),
      ),
      navigationBarTheme: NavigationBarThemeData(
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        height: 68,
        indicatorColor: accentColor.withValues(alpha: 0.18),
        indicatorShape: pill,
        labelBehavior: NavigationDestinationLabelBehavior.alwaysShow,
        iconTheme: WidgetStateProperty.resolveWith((states) => IconThemeData(
          color: states.contains(WidgetState.selected) ? accentColor : muted,
        )),
        labelTextStyle: WidgetStateProperty.resolveWith((states) => TextStyle(
          fontSize: 12,
          fontWeight: states.contains(WidgetState.selected) ? FontWeight.w800 : FontWeight.w600,
          color: states.contains(WidgetState.selected) ? text : muted,
        )),
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(22)),
      ),
      snackBarTheme: SnackBarThemeData(
        behavior: SnackBarBehavior.floating,
        backgroundColor: surface2,
        contentTextStyle: TextStyle(color: text, fontWeight: FontWeight.w600),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      ),
      popupMenuTheme: PopupMenuThemeData(
        color: surface,
        surfaceTintColor: Colors.transparent,
        elevation: 6,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(18),
          side: BorderSide(color: border),
        ),
        textStyle: TextStyle(color: text, fontWeight: FontWeight.w600),
      ),
      progressIndicatorTheme: ProgressIndicatorThemeData(color: accentColor),
      dividerTheme: DividerThemeData(color: border, space: 1),
    );
  }
}
