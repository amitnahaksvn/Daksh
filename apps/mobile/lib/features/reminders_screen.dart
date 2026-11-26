import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/app_models.dart';
import '../core/providers.dart';
import '../core/widgets.dart';
import 'provider_widgets.dart';

String _when(DateTime? d) {
  if (d == null) return '';
  String two(int n) => n.toString().padLeft(2, '0');
  return '${d.year}-${two(d.month)}-${two(d.day)} ${two(d.hour)}:${two(d.minute)}';
}

class RemindersScreen extends ConsumerWidget {
  const RemindersScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => Scaffold(
    floatingActionButton: FloatingActionButton.extended(
      onPressed: () => _add(context, ref),
      icon: const Icon(Icons.add_alarm),
      label: const Text('Add reminder'),
    ),
    body: AsyncBody<List<ReminderItem>>(
      value: ref.watch(remindersProvider),
      onRetry: () => ref.invalidate(remindersProvider),
      builder: (items) => items.isEmpty
          ? const Center(child: Text('No reminders yet.'))
          : RefreshIndicator(
              onRefresh: () async => ref.invalidate(remindersProvider),
              child: ListView(
                padding: const EdgeInsets.fromLTRB(12, 4, 12, 88),
                children: [
                  Tile(
                    child: Column(
                      children: [
                        for (final r in items)
                          ListTile(
                            leading: const Icon(Icons.alarm),
                            title: Text(r.text),
                            subtitle: Text(_when(r.remindAt)),
                          ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
    ),
  );

  Future<void> _add(BuildContext context, WidgetRef ref) async {
    final text = TextEditingController();
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('New reminder'),
        content: TextField(
          controller: text,
          autofocus: true,
          decoration: const InputDecoration(
            labelText: 'What should I remind you about?',
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(c, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(c, true),
            child: const Text('Next'),
          ),
        ],
      ),
    );
    if (ok != true || text.text.trim().isEmpty || !context.mounted) return;
    final now = DateTime.now();
    final date = await showDatePicker(
      context: context,
      initialDate: now,
      firstDate: now,
      lastDate: DateTime(now.year + 5),
    );
    if (date == null || !context.mounted) return;
    final time = await showTimePicker(
      context: context,
      initialTime: TimeOfDay.fromDateTime(now.add(const Duration(hours: 1))),
    );
    if (time == null) return;
    final at = DateTime(
      date.year,
      date.month,
      date.day,
      time.hour,
      time.minute,
    );
    try {
      await ref.read(apiProvider).createReminder(text.text.trim(), at);
      ref.invalidate(remindersProvider);
    } catch (e) {
      if (context.mounted)
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(errorText(e))));
    }
  }
}
