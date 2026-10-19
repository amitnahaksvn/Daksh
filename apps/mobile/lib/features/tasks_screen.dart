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
              : ListView(children: [
                  for (final t in tasks)
                    ListTile(
                      title: Text(t.title, style: TextStyle(decoration: t.completed ? TextDecoration.lineThrough : null)),
                    ),
                ]),
        ),
      );

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
