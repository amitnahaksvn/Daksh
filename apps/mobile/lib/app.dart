import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'core/theme.dart';
import 'core/theme_mode.dart';
import 'features/assistant_screen.dart';
import 'features/integrations_screen.dart';
import 'features/privacy_screen.dart';
import 'features/tasks_screen.dart';

const destinations = <(String, String, IconData, Widget)>[
  ('/assistant', 'Assistant', Icons.chat_bubble_outline, AssistantScreen()),
  ('/tasks', 'Tasks', Icons.checklist, TasksScreen()),
  ('/integrations', 'Integrations', Icons.extension_outlined, IntegrationsScreen()),
  ('/privacy', 'Privacy', Icons.lock_outline, PrivacyScreen()),
];

GoRouter buildRouter() => GoRouter(
  initialLocation: '/assistant',
  routes: [
    ShellRoute(
      builder: (context, state, child) {
        final index = destinations.indexWhere((d) => state.uri.path.startsWith(d.$1));
        final selected = index < 0 ? 0 : index;
        return Scaffold(
          appBar: AppBar(
            title: Row(children: [
              const _LotusMark(size: 28),
              const SizedBox(width: 10),
              Text('Daksha · ${destinations[selected].$2}'),
            ]),
            actions: const [_AppearanceMenu(), SizedBox(width: 8)],
          ),
          body: child,
          bottomNavigationBar: NavigationBar(
            selectedIndex: selected,
            onDestinationSelected: (i) => context.go(destinations[i].$1),
            destinations: [
              for (final d in destinations)
                NavigationDestination(icon: Icon(d.$3), label: d.$2),
            ],
          ),
        );
      },
      routes: [
        for (final d in destinations) GoRoute(path: d.$1, builder: (_, _) => d.$4),
      ],
    ),
  ],
);

class _LotusMark extends StatelessWidget {
  const _LotusMark({required this.size});
  final double size;

  @override
  Widget build(BuildContext context) => CustomPaint(
    size: Size(size, size),
    painter: _LotusPainter(Theme.of(context).colorScheme.primary),
  );
}

class _LotusPainter extends CustomPainter {
  const _LotusPainter(this.color);
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    final scale = size.width / 128;
    canvas.save();
    canvas.scale(scale);
    final paint = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = 4
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round;
    final paths = [
      Path()..moveTo(64, 18)..cubicTo(48, 38, 48, 61, 64, 83)..cubicTo(80, 61, 80, 38, 64, 18),
      Path()..moveTo(58, 78)..cubicTo(35, 72, 22, 54, 22, 39)..cubicTo(42, 39, 58, 51, 64, 72),
      Path()..moveTo(70, 78)..cubicTo(93, 72, 106, 54, 106, 39)..cubicTo(86, 39, 70, 51, 64, 72),
      Path()..moveTo(62, 84)..cubicTo(39, 88, 21, 81, 12, 66)..cubicTo(31, 58, 51, 64, 64, 80),
      Path()..moveTo(66, 84)..cubicTo(89, 88, 107, 81, 116, 66)..cubicTo(97, 58, 77, 64, 64, 80),
      Path()..moveTo(16, 91)..cubicTo(42, 82, 86, 82, 112, 91),
    ];
    for (final path in paths) {
      canvas.drawPath(path, paint);
      final glow = Paint()
        ..color = color.withValues(alpha: 0.18)
        ..style = PaintingStyle.stroke
        ..strokeWidth = 8
        ..maskFilter = const MaskFilter.blur(BlurStyle.normal, 5);
      canvas.drawPath(path, glow);
    }
    canvas.restore();
  }

  @override
  bool shouldRepaint(covariant _LotusPainter oldDelegate) => oldDelegate.color != color;
}

class _AppearanceMenu extends ConsumerWidget {
  const _AppearanceMenu();

  Future<void> _pickCustom(BuildContext context, WidgetRef ref) async {
    final controller = TextEditingController(text: '#8B5CF6');
    final value = await showDialog<String>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Custom accent colour'),
        content: TextField(
          controller: controller,
          maxLength: 7,
          textCapitalization: TextCapitalization.characters,
          decoration: const InputDecoration(labelText: 'HEX colour', hintText: '#8B5CF6'),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancel')),
          FilledButton(
            onPressed: () {
              final hex = controller.text.trim();
              if (RegExp(r'^#[0-9a-fA-F]{6}$').hasMatch(hex)) Navigator.pop(context, hex);
            },
            child: const Text('Apply'),
          ),
        ],
      ),
    );
    if (value != null) ref.read(accentColorProvider.notifier).set(value);
    controller.dispose();
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mode = ref.watch(themeModeProvider);
    final style = ref.watch(themeStyleProvider);
    final accent = ref.watch(accentColorProvider);
    return PopupMenuButton<String>(
      tooltip: 'Appearance and colours',
      icon: const Icon(Icons.palette_outlined),
      onSelected: (value) {
        final parts = value.split(':');
        if (parts.length != 2) return;
        switch (parts[0]) {
          case 'mode':
            ref.read(themeModeProvider.notifier).set(
              ThemeMode.values.firstWhere((m) => m.name == parts[1], orElse: () => ThemeMode.system),
            );
          case 'style':
            ref.read(themeStyleProvider.notifier).set(parts[1]);
          case 'accent':
            if (parts[1] == 'custom') {
              _pickCustom(context, ref);
            } else {
              ref.read(accentColorProvider.notifier).set(parts[1]);
            }
        }
      },
      itemBuilder: (_) => [
        const PopupMenuItem(enabled: false, child: Text('THEME STYLE')),
        CheckedPopupMenuItem(value: 'style:bento', checked: style == 'bento', child: const Text('Bento')),
        CheckedPopupMenuItem(value: 'style:neural', checked: style == 'neural', child: const Text('Neural')),
        CheckedPopupMenuItem(value: 'style:aurora', checked: style == 'aurora', child: const Text('Aurora')),
        const PopupMenuDivider(),
        const PopupMenuItem(enabled: false, child: Text('COLOUR PALETTE')),
        for (final p in const {'violet': 'Violet', 'blue': 'Ocean blue', 'emerald': 'Emerald', 'rose': 'Rose', 'amber': 'Amber'}.entries)
          CheckedPopupMenuItem(value: 'accent:${p.key}', checked: accent == p.key, child: Text(p.value)),
        const PopupMenuItem(value: 'accent:custom', child: Text('Choose custom HEX colour…')),
        const PopupMenuDivider(),
        const PopupMenuItem(enabled: false, child: Text('LIGHT MODE')),
        CheckedPopupMenuItem(value: 'mode:system', checked: mode == ThemeMode.system, child: const Text('Follow device')),
        CheckedPopupMenuItem(value: 'mode:light', checked: mode == ThemeMode.light, child: const Text('Day')),
        CheckedPopupMenuItem(value: 'mode:dark', checked: mode == ThemeMode.dark, child: const Text('Night')),
      ],
    );
  }
}

class DakshApp extends ConsumerStatefulWidget {
  const DakshApp({super.key});
  @override
  ConsumerState<DakshApp> createState() => _DakshAppState();
}

class _DakshAppState extends ConsumerState<DakshApp> {
  final _router = buildRouter();

  @override
  Widget build(BuildContext context) {
    final style = ref.watch(themeStyleProvider);
    final accent = ref.watch(accentColorProvider);
    return MaterialApp.router(
      title: 'Daksha — The Prajapati',
      theme: AppTheme.light(style: style, palette: accent),
      darkTheme: AppTheme.dark(style: style, palette: accent),
      themeMode: ref.watch(themeModeProvider),
      routerConfig: _router,
    );
  }
}
