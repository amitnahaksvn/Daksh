import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

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
              appBar: AppBar(title: Text(destinations[index < 0 ? 0 : index].$2)),
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

class DakshApp extends StatelessWidget {
  DakshApp({super.key});
  final _router = buildRouter();

  @override
  Widget build(BuildContext context) => MaterialApp.router(
        title: 'Daksh',
        theme: ThemeData(colorSchemeSeed: Colors.indigo, useMaterial3: true),
        darkTheme: ThemeData(colorSchemeSeed: Colors.indigo, brightness: Brightness.dark, useMaterial3: true),
        routerConfig: _router,
      );
}
