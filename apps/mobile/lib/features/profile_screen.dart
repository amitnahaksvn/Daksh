import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/app_models.dart';
import '../core/providers.dart';
import '../core/session.dart';
import '../core/widgets.dart';
import 'provider_widgets.dart';

/// Your details, your password, and "I'm busy" mode.
class ProfileScreen extends ConsumerWidget {
  const ProfileScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => AsyncBody<UserDetails>(
    value: ref.watch(meProvider),
    onRetry: () => ref.invalidate(meProvider),
    builder: (me) => ListView(
      padding: const EdgeInsets.all(12),
      children: [
        Tile(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(me.fullName, style: Theme.of(context).textTheme.titleLarge),
              Text(me.email),
              const SizedBox(height: 12),
              Wrap(
                spacing: 8,
                children: [
                  OutlinedButton.icon(
                    onPressed: () => _edit(context, ref, me),
                    icon: const Icon(Icons.edit_outlined),
                    label: const Text('Edit details'),
                  ),
                  OutlinedButton.icon(
                    onPressed: () => _password(context, ref),
                    icon: const Icon(Icons.key_outlined),
                    label: const Text('Change password'),
                  ),
                ],
              ),
            ],
          ),
        ),
        const SizedBox(height: 10),
        const _BusyCard(),
        const SizedBox(height: 10),
        Tile(
          child: ListTile(
            leading: const Icon(Icons.logout),
            title: const Text('Sign out'),
            onTap: () => ref.read(sessionProvider.notifier).signOut(),
          ),
        ),
      ],
    ),
  );

  Future<void> _edit(
    BuildContext context,
    WidgetRef ref,
    UserDetails me,
  ) async {
    final first = TextEditingController(text: me.firstName),
        last = TextEditingController(text: me.lastName);
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('Edit details'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextField(
              controller: first,
              decoration: const InputDecoration(labelText: 'First name'),
            ),
            TextField(
              controller: last,
              decoration: const InputDecoration(labelText: 'Last name'),
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
            child: const Text('Save'),
          ),
        ],
      ),
    );
    if (ok != true || first.text.trim().isEmpty || !context.mounted) return;
    try {
      await ref
          .read(apiProvider)
          .updateMe(
            first.text.trim(),
            last.text.trim(),
            me.gender,
            me.dateOfBirth,
          );
      ref.invalidate(meProvider);
    } catch (e) {
      if (context.mounted)
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(errorText(e))));
    }
  }

  Future<void> _password(BuildContext context, WidgetRef ref) async {
    final current = TextEditingController(), next = TextEditingController();
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('Change password'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextField(
              controller: current,
              obscureText: true,
              decoration: const InputDecoration(labelText: 'Current password'),
            ),
            TextField(
              controller: next,
              obscureText: true,
              decoration: const InputDecoration(labelText: 'New password'),
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
            child: const Text('Change'),
          ),
        ],
      ),
    );
    if (ok != true || !context.mounted) return;
    try {
      await ref.read(apiProvider).changePassword(current.text, next.text);
      if (context.mounted)
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(const SnackBar(content: Text('Password changed.')));
    } catch (e) {
      if (context.mounted)
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(errorText(e))));
    }
  }
}

/// "I'm busy": while it is on, auto-reply turns itself on for every connected app. The details (hours, message) are kept as they are.
class _BusyCard extends ConsumerStatefulWidget {
  const _BusyCard();
  @override
  ConsumerState<_BusyCard> createState() => _BusyCardState();
}

class _BusyCardState extends ConsumerState<_BusyCard> {
  Map<String, dynamic>? _busy;
  String? _error;

  @override
  void initState() {
    super.initState();
    ref
        .read(apiProvider)
        .busy()
        .then(
          (b) => mounted ? setState(() => _busy = b) : null,
          onError: (Object e) =>
              mounted ? setState(() => _error = errorText(e)) : null,
        );
  }

  Future<void> _set(bool on) async {
    final next = {...?_busy, 'enabled': on}..remove('isBusyNow');
    try {
      final saved = await ref.read(apiProvider).updateBusy(next);
      if (mounted)
        setState(() {
          _busy = saved;
          _error = null;
        });
    } catch (e) {
      if (mounted) setState(() => _error = errorText(e));
    }
  }

  @override
  Widget build(BuildContext context) => Tile(
    padding: const EdgeInsets.all(4),
    child: _busy == null
        ? ListTile(
            title: const Text("I'm busy"),
            subtitle: Text(_error ?? 'Loading…'),
          )
        : SwitchListTile(
            title: const Text("I'm busy"),
            subtitle: Text(
              _busy!['isBusyNow'] == true
                  ? 'Busy now: auto-reply is on for your apps.'
                  : 'Turns on auto-reply for every connected app during your busy hours.',
            ),
            value: _busy!['enabled'] == true,
            onChanged: _set,
          ),
  );
}
