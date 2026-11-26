import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/app_models.dart';
import '../core/providers.dart';
import '../core/widgets.dart';

String _stamp(DateTime? d) {
  if (d == null) return '';
  String two(int n) => n.toString().padLeft(2, '0');
  return '${d.day}/${d.month} ${two(d.hour)}:${two(d.minute)}';
}

/// The web's Notifications and Activity pages: recent messages across your chat apps, and what Daksha did on your behalf.
class ActivityScreen extends ConsumerWidget {
  const ActivityScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => DefaultTabController(
    length: 2,
    child: Column(
      children: [
        const TabBar(
          tabs: [
            Tab(text: 'Notifications'),
            Tab(text: 'Activity log'),
          ],
        ),
        Expanded(
          child: TabBarView(
            children: [
              AsyncBody<List<ActivityEntry>>(
                value: ref.watch(activityProvider),
                onRetry: () => ref.invalidate(activityProvider),
                builder: (items) => items.isEmpty
                    ? const Center(
                        child: Text(
                          'Nothing yet. New WhatsApp and Telegram messages will show up here.',
                        ),
                      )
                    : RefreshIndicator(
                        onRefresh: () async => ref.invalidate(activityProvider),
                        child: ListView.separated(
                          padding: const EdgeInsets.all(12),
                          itemCount: items.length,
                          separatorBuilder: (_, _) => const Divider(height: 1),
                          itemBuilder: (_, i) => ListTile(
                            leading: Icon(
                              items[i].fromMe
                                  ? Icons.north_east
                                  : Icons.south_west,
                            ),
                            title: Text(
                              items[i].chat.isEmpty
                                  ? items[i].app
                                  : items[i].chat,
                            ),
                            subtitle: Text(
                              items[i].text,
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                            ),
                            trailing: Text(
                              _stamp(items[i].at),
                              style: Theme.of(context).textTheme.bodySmall,
                            ),
                          ),
                        ),
                      ),
              ),
              AsyncBody<List<AuditEvent>>(
                value: ref.watch(auditProvider),
                onRetry: () => ref.invalidate(auditProvider),
                builder: (items) => items.isEmpty
                    ? const Center(child: Text('No activity yet.'))
                    : ListView.separated(
                        padding: const EdgeInsets.all(12),
                        itemCount: items.length,
                        separatorBuilder: (_, _) => const Divider(height: 1),
                        itemBuilder: (_, i) => ListTile(
                          title: Text(items[i].action),
                          subtitle: items[i].detail == null
                              ? null
                              : Text(items[i].detail!),
                          trailing: Text(
                            _stamp(items[i].at),
                            style: Theme.of(context).textTheme.bodySmall,
                          ),
                        ),
                      ),
              ),
            ],
          ),
        ),
      ],
    ),
  );
}
