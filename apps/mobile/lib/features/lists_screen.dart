import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/app_models.dart';
import '../core/providers.dart';
import '../core/widgets.dart';
import 'provider_widgets.dart';

/// Shopping, packing, anything: named lists with tick-off items.
class ListsScreen extends ConsumerWidget {
  const ListsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => Scaffold(
    floatingActionButton: FloatingActionButton.extended(
      onPressed: () => _add(context, ref),
      icon: const Icon(Icons.add),
      label: const Text('Add item'),
    ),
    body: AsyncBody<List<ListEntry>>(
      value: ref.watch(listsProvider),
      onRetry: () => ref.invalidate(listsProvider),
      builder: (items) {
        if (items.isEmpty)
          return const Center(
            child: Text('No lists yet. Add an item to start one.'),
          );
        final byList = <String, List<ListEntry>>{};
        for (final i in items) {
          byList.putIfAbsent(i.list, () => []).add(i);
        }
        return RefreshIndicator(
          onRefresh: () async => ref.invalidate(listsProvider),
          child: ListView(
            padding: const EdgeInsets.fromLTRB(12, 4, 12, 88),
            children: [
              for (final e in byList.entries)
                Padding(
                  padding: const EdgeInsets.only(bottom: 10),
                  child: Tile(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Padding(
                          padding: const EdgeInsets.fromLTRB(16, 12, 16, 0),
                          child: Text(
                            e.key,
                            style: Theme.of(context).textTheme.titleMedium,
                          ),
                        ),
                        for (final i in e.value)
                          Dismissible(
                            key: ValueKey(i.id),
                            direction: DismissDirection.endToStart,
                            background: Container(
                              color: Theme.of(
                                context,
                              ).colorScheme.errorContainer,
                              alignment: Alignment.centerRight,
                              padding: const EdgeInsets.only(right: 20),
                              child: const Icon(Icons.delete_outline),
                            ),
                            onDismissed: (_) => _run(
                              context,
                              ref,
                              () => ref.read(apiProvider).deleteListItem(i.id),
                            ),
                            child: CheckboxListTile(
                              value: i.done,
                              controlAffinity: ListTileControlAffinity.leading,
                              title: Text(
                                i.text,
                                style: TextStyle(
                                  decoration: i.done
                                      ? TextDecoration.lineThrough
                                      : null,
                                ),
                              ),
                              onChanged: (_) => _run(
                                context,
                                ref,
                                () =>
                                    ref.read(apiProvider).toggleListItem(i.id),
                              ),
                            ),
                          ),
                      ],
                    ),
                  ),
                ),
            ],
          ),
        );
      },
    ),
  );

  Future<void> _run(
    BuildContext context,
    WidgetRef ref,
    Future<void> Function() action,
  ) async {
    try {
      await action();
    } catch (e) {
      if (context.mounted)
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(errorText(e))));
    }
    ref.invalidate(listsProvider);
  }

  Future<void> _add(BuildContext context, WidgetRef ref) async {
    final list = TextEditingController(text: 'Shopping'),
        text = TextEditingController();
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('Add to a list'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextField(
              controller: list,
              decoration: const InputDecoration(labelText: 'List'),
            ),
            TextField(
              controller: text,
              autofocus: true,
              decoration: const InputDecoration(labelText: 'Item'),
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(c, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(c, true),
            child: const Text('Add'),
          ),
        ],
      ),
    );
    if (ok != true ||
        text.text.trim().isEmpty ||
        list.text.trim().isEmpty ||
        !context.mounted)
      return;
    await _run(
      context,
      ref,
      () =>
          ref.read(apiProvider).addListItem(list.text.trim(), text.text.trim()),
    );
  }
}
