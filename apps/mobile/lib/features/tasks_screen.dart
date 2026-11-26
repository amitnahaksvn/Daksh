import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/providers.dart';
import '../core/widgets.dart';

class TasksScreen extends ConsumerWidget {
  const TasksScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => Scaffold(
        floatingActionButton: FloatingActionButton.extended(
          onPressed: () => _add(context, ref),
          icon: const Icon(Icons.add),
          label: const Text('Add task'),
        ),
        body: AsyncBody(
          value: ref.watch(tasksProvider),
          onRetry: () => ref.invalidate(tasksProvider),
          builder: (tasks) => tasks.isEmpty
              ? const Center(child: Text('No tasks yet.'))
              : ListView(padding: const EdgeInsets.fromLTRB(12, 4, 12, 88), children: [
                  Tile(
                    child: Column(children: [
                      for (var i = 0; i < tasks.length; i++) ...[
                        if (i > 0) const Divider(indent: 16, endIndent: 16),
                        ListTile(
                          leading: Icon(tasks[i].completed ? Icons.check_circle : Icons.radio_button_unchecked,
                              color: tasks[i].completed ? Theme.of(context).colorScheme.primary : null),
                          title: Text(tasks[i].title, style: TextStyle(decoration: tasks[i].completed ? TextDecoration.lineThrough : null)),
                          onTap: tasks[i].completed ? null : () => _complete(context, ref, tasks[i].id),
                        ),
                      ],
                    ]),
                  ),
                ]),
        ),
      );

  Future<void> _complete(BuildContext context, WidgetRef ref, String id) async {
    try {
      await ref.read(apiProvider).completeTask(id);
      ref.invalidate(tasksProvider);
    } catch (e) {
      if (context.mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.toString())));
    }
  }

  Future<void> _add(BuildContext context, WidgetRef ref) async {
    final controller = TextEditingController();
    final title = await showDialog<String>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('New task'),
        content: TextField(controller: controller, autofocus: true, decoration: const InputDecoration(labelText: 'Title')),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(c, controller.text), child: const Text('Add')),
        ],
      ),
    );
    if (title == null || title.trim().isEmpty) return;
    try {
      await ref.read(apiProvider).createTask(title.trim());
      ref.invalidate(tasksProvider);
    } catch (e) {
      if (context.mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.toString())));
    }
  }
}
