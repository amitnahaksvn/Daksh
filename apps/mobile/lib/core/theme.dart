import 'package:flutter/material.dart';

/// Daksha's Bento, Neural and Aurora visual themes.
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
    final isDarkCanvas = isDark || style == 'neural' || style == 'aurora';
    final canvas = style == 'neural'
        ? const Color(0xFF06070F)
        : style == 'aurora'
            ? const Color(0xFF0B1020)
            : isDark
                ? const Color(0xFF111214)
                : const Color(0xFFECEEF2);
    final surface = style == 'neural'
        ? const Color(0xFF101224)
        : style == 'aurora'
            ? const Color(0xFF0F1220)
            : isDark
                ? const Color(0xFF1B1C20)
                : Colors.white;
    final surface2 = style == 'neural'
        ? const Color(0xFF202238)
        : style == 'aurora'
            ? const Color(0xFF202640)
            : isDark
                ? const Color(0xFF26282D)
                : const Color(0xFFF4F5F7);
    final text = isDarkCanvas ? const Color(0xFFF2F3F5) : ink;
    final muted = isDarkCanvas ? const Color(0xFF9A9EA8) : const Color(0xFF5A5D66);
    final border = isDarkCanvas ? const Color(0x17FFFFFF) : const Color(0x1417181C);
    final primary = accentColor;
    const onPrimary = Colors.white;

    final scheme = ColorScheme(
      brightness: isDarkCanvas ? Brightness.dark : brightness,
      primary: primary,
      onPrimary: onPrimary,
      primaryContainer: accentColor.withValues(alpha: 0.18),
      onPrimaryContainer: isDarkCanvas ? Colors.white : accentColor,
      secondary: accentColor.withValues(alpha: 0.72),
      onSecondary: Colors.white,
      secondaryContainer: accentColor.withValues(alpha: 0.16),
      onSecondaryContainer: isDarkCanvas ? Colors.white : accentColor,
      tertiary: lime,
      onTertiary: ink,
      error: isDarkCanvas ? const Color(0xFFFFB4AB) : const Color(0xFFB3261E),
      onError: isDarkCanvas ? ink : Colors.white,
      errorContainer: isDarkCanvas ? const Color(0xFF3A1D1D) : const Color(0xFFFFDAD6),
      onErrorContainer: isDarkCanvas ? const Color(0xFFFFB4AB) : const Color(0xFF7A1F1F),
      surface: surface,
      onSurface: text,
      onSurfaceVariant: muted,
      outline: isDarkCanvas ? const Color(0xFF3A3D44) : const Color(0xFFCFD3DA),
      outlineVariant: border,
      surfaceContainerLowest: isDarkCanvas ? const Color(0xFF111214) : Colors.white,
      surfaceContainerLow: surface,
      surfaceContainer: surface2,
      surfaceContainerHigh: surface2,
      surfaceContainerHighest: isDarkCanvas ? const Color(0xFF30333A) : const Color(0xFFEAECF0),
      inverseSurface: isDarkCanvas ? const Color(0xFFF2F3F5) : ink,
      onInverseSurface: isDarkCanvas ? ink : Colors.white,
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
          borderRadius: BorderRadius.circular(18),
          borderSide: BorderSide(color: color, width: width),
        );

    return ThemeData(
      useMaterial3: true,
      brightness: isDarkCanvas ? Brightness.dark : brightness,
      colorScheme: scheme,
      scaffoldBackgroundColor: canvas,
      appBarTheme: AppBarTheme(
        backgroundColor: canvas,
        foregroundColor: text,
        elevation: 0,
        scrolledUnderElevation: 0,
        surfaceTintColor: Colors.transparent,
        titleTextStyle: TextStyle(
          color: text,
          fontSize: 22,
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
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          foregroundColor: text,
          shape: pill,
          textStyle: const TextStyle(fontWeight: FontWeight.w700),
        ),
      ),
      navigationBarTheme: NavigationBarThemeData(
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        height: 68,
        indicatorColor: scheme.primaryContainer,
        indicatorShape: pill,
        labelBehavior: NavigationDestinationLabelBehavior.alwaysShow,
        iconTheme: WidgetStateProperty.resolveWith(
          (states) => IconThemeData(
            color: states.contains(WidgetState.selected)
                ? scheme.onPrimaryContainer
                : muted,
          ),
        ),
        labelTextStyle: WidgetStateProperty.resolveWith(
          (states) => TextStyle(
            fontSize: 12,
            fontWeight: states.contains(WidgetState.selected)
                ? FontWeight.w800
                : FontWeight.w600,
            color: states.contains(WidgetState.selected) ? text : muted,
          ),
        ),
      ),
      floatingActionButtonTheme: FloatingActionButtonThemeData(
        backgroundColor: accentColor,
        foregroundColor: onPrimary,
        elevation: 0,
        highlightElevation: 0,
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(26)),
      ),
      snackBarTheme: SnackBarThemeData(
        behavior: SnackBarBehavior.floating,
        backgroundColor: isDarkCanvas ? const Color(0xFFF2F3F5) : ink,
        contentTextStyle: TextStyle(
          color: isDarkCanvas ? ink : Colors.white,
          fontWeight: FontWeight.w600,
        ),
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
      switchTheme: SwitchThemeData(
        thumbColor: WidgetStateProperty.resolveWith(
          (states) => states.contains(WidgetState.selected) ? onPrimary : muted,
        ),
        trackColor: WidgetStateProperty.resolveWith(
          (states) => states.contains(WidgetState.selected) ? primary : surface2,
        ),
        trackOutlineColor: WidgetStatePropertyAll(border),
      ),
      progressIndicatorTheme: ProgressIndicatorThemeData(color: primary),
      dividerTheme: DividerThemeData(color: border, space: 1),
    );
  }
}
