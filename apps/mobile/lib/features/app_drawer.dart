import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../core/providers.dart';
import '../core/session.dart';

/// ChatGPT's side menu: new chat, your past chats, then the rest of the app.
class AppDrawer extends ConsumerWidget {
  const AppDrawer({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final scheme = Theme.of(context).colorScheme;
    final history = ref.watch(conversationsProvider);
    final current = ref.watch(assistantProvider).conversationId;
    void go(String path) { Navigator.pop(context); context.push(path); }

    return Drawer(
      backgroundColor: Theme.of(context).brightness == Brightness.dark ? const Color(0xFF171717) : const Color(0xFFF9F9F9),
      child: SafeArea(
        child: Column(children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(8, 12, 8, 4),
            child: ListTile(
              leading: const Icon(Icons.edit_square, size: 22),
              title: const Text('New chat', style: TextStyle(fontWeight: FontWeight.w600)),
              onTap: () { ref.read(assistantProvider.notifier).newChat(); Navigator.pop(context); },
            ),
          ),
          for (final (label, icon, path) in const [
            ('Dashboard', Icons.dashboard_outlined, '/dashboard'),
            ('Chats', Icons.forum_outlined, '/chats'),
            ('Calendar', Icons.calendar_month_outlined, '/calendar'),
            ('Tasks', Icons.checklist, '/tasks'),
          ])
            ListTile(leading: Icon(icon, size: 22), title: Text(label), onTap: () => go(path)),
          const Divider(height: 24),
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 6),
            child: Align(alignment: Alignment.centerLeft, child: Text('Chats', style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13, fontWeight: FontWeight.w600))),
          ),
          Expanded(
            child: history.when(
              loading: () => const Center(child: CircularProgressIndicator(strokeWidth: 2)),
              error: (e, _) => Padding(padding: const EdgeInsets.all(20), child: Text(e.toString(), style: TextStyle(color: scheme.error, fontSize: 13))),
              data: (list) => list.isEmpty
                  ? Padding(padding: const EdgeInsets.all(20), child: Text('No chats yet.', style: TextStyle(color: scheme.onSurfaceVariant)))
                  : ListView(children: [
                      for (final c in list)
                        ListTile(
                          dense: true,
                          selected: c.id == current,
                          selectedTileColor: scheme.surfaceContainer,
                          title: Text(c.title, maxLines: 1, overflow: TextOverflow.ellipsis),
                          onTap: () { ref.read(assistantProvider.notifier).open(c.id); Navigator.pop(context); },
                        ),
                    ]),
            ),
          ),
          const Divider(height: 1),
          ListTile(leading: const Icon(Icons.extension_outlined), title: const Text('Integrations'), onTap: () => go('/more/integrations')),
          ListTile(leading: const Icon(Icons.lock_outline), title: const Text('Privacy'), onTap: () => go('/more/privacy')),
          ListTile(leading: const Icon(Icons.logout), title: const Text('Sign out'), onTap: () => ref.read(sessionProvider.notifier).signOut()),
        ]),
      ),
    );
  }
}
