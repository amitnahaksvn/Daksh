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
            return Scaffold(
              appBar: AppBar(title: Text(destinations[index < 0 ? 0 : index].$2), actions: const [_AppearanceMenu(), SizedBox(width: 8)]),
              body: child,
              bottomNavigationBar: NavigationBar(
                selectedIndex: index < 0 ? 0 : index,
                onDestinationSelected: (i) => context.go(destinations[i].$1),
                destinations: [for (final d in destinations) NavigationDestination(icon: Icon(d.$3), label: d.$2)],
              ),
            );
          },
          routes: [
            for (final d in destinations) GoRoute(path: d.$1, builder: (_, _) => d.$4),
          ],
        ),
      ],
    );

/// Day / Night / Auto, from the app bar.
class _AppearanceMenu extends ConsumerWidget {
  const _AppearanceMenu();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mode = ref.watch(themeModeProvider);
    return PopupMenuButton<ThemeMode>(
      tooltip: 'Appearance',
      icon: Icon(switch (mode) {
        ThemeMode.light => Icons.light_mode_outlined,
        ThemeMode.dark => Icons.dark_mode_outlined,
        ThemeMode.system => Icons.brightness_auto_outlined,
      }),
      initialValue: mode,
      onSelected: (m) => ref.read(themeModeProvider.notifier).set(m),
      itemBuilder: (_) => const [
        PopupMenuItem(value: ThemeMode.light, child: Text('Day')),
        PopupMenuItem(value: ThemeMode.dark, child: Text('Night')),
        PopupMenuItem(value: ThemeMode.system, child: Text('Auto (follow phone)')),
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
  Widget build(BuildContext context) => MaterialApp.router(
        title: 'Daksh',
        theme: AppTheme.light(),
        darkTheme: AppTheme.dark(),
        themeMode: ref.watch(themeModeProvider),
        routerConfig: _router,
      );
}
