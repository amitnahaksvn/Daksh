import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/app_models.dart';
import '../core/providers.dart';
import '../core/widgets.dart';
import 'provider_widgets.dart';

const _filters = [
  ('Inbox', 'in:inbox'),
  ('Unread', 'is:unread'),
  ('Starred', 'is:starred'),
  ('Sent', 'in:sent'),
];

/// The web's Email page: your Gmail inbox, a message, and a new message.
class EmailScreen extends ConsumerStatefulWidget {
  const EmailScreen({super.key});
  @override
  ConsumerState<EmailScreen> createState() => _EmailScreenState();
}

class _EmailScreenState extends ConsumerState<EmailScreen> {
  String _query = _filters.first.$2;

  @override
  Widget build(BuildContext context) => Scaffold(
    floatingActionButton: FloatingActionButton.extended(
      onPressed: () => _compose(context),
      icon: const Icon(Icons.edit_outlined),
      label: const Text('Write'),
    ),
    body: Column(
      children: [
        SizedBox(
          height: 52,
          child: ListView(
            scrollDirection: Axis.horizontal,
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
            children: [
              for (final (label, q) in _filters)
                Padding(
                  padding: const EdgeInsets.only(right: 8),
                  child: ChoiceChip(
                    label: Text(label),
                    selected: q == _query,
                    onSelected: (_) => setState(() => _query = q),
                  ),
                ),
            ],
          ),
        ),
        Expanded(
          child: AsyncBody<MailPage>(
            value: ref.watch(mailProvider(_query)),
            onRetry: () => ref.invalidate(mailProvider(_query)),
            builder: (page) => page.messages.isEmpty
                ? const Center(child: Text('No messages.'))
                : RefreshIndicator(
                    onRefresh: () async => ref.invalidate(mailProvider(_query)),
                    child: ListView.separated(
                      padding: const EdgeInsets.fromLTRB(12, 0, 12, 88),
                      itemCount: page.messages.length,
                      separatorBuilder: (_, _) => const Divider(height: 1),
                      itemBuilder: (context, i) {
                        final m = page.messages[i];
                        return ListTile(
                          title: Text(
                            m.subject,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(
                              fontWeight: m.unread
                                  ? FontWeight.w800
                                  : FontWeight.w500,
                            ),
                          ),
                          subtitle: Text(
                            '${m.from}\n${m.snippet}',
                            maxLines: 2,
                            overflow: TextOverflow.ellipsis,
                          ),
                          isThreeLine: true,
                          onTap: () => Navigator.of(context).push(
                            MaterialPageRoute<void>(
                              builder: (_) => MailPageView(id: m.id),
                            ),
                          ),
                        );
                      },
                    ),
                  ),
          ),
        ),
      ],
    ),
  );

  Future<void> _compose(BuildContext context) async {
    final to = TextEditingController(),
        subject = TextEditingController(),
        body = TextEditingController();
    final send = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('New message'),
        content: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(
                controller: to,
                keyboardType: TextInputType.emailAddress,
                decoration: const InputDecoration(labelText: 'To'),
              ),
              TextField(
                controller: subject,
                decoration: const InputDecoration(labelText: 'Subject'),
              ),
              TextField(
                controller: body,
                minLines: 4,
                maxLines: 8,
                decoration: const InputDecoration(labelText: 'Message'),
              ),
            ],
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(c, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(c, true),
            child: const Text('Send'),
          ),
        ],
      ),
    );
    if (send != true || to.text.trim().isEmpty) return;
    try {
      await ref
          .read(apiProvider)
          .sendMail(to.text.trim(), subject.text.trim(), body.text);
      if (context.mounted)
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(const SnackBar(content: Text('Sent.')));
    } catch (e) {
      if (context.mounted)
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(errorText(e))));
    }
  }
}

/// One email, read on its own page.
class MailPageView extends ConsumerWidget {
  const MailPageView({super.key, required this.id});
  final String id;

  @override
  Widget build(BuildContext context, WidgetRef ref) => Scaffold(
    appBar: AppBar(title: const Text('Email')),
    body: FutureBuilder<MailMessage>(
      future: ref.read(apiProvider).mailMessage(id),
      builder: (context, snap) {
        if (snap.connectionState != ConnectionState.done)
          return const Center(child: CircularProgressIndicator());
        if (snap.hasError)
          return Center(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Text(errorText(snap.error!)),
            ),
          );
        final m = snap.data!;
        return ListView(
          padding: const EdgeInsets.all(16),
          children: [
            Text(m.subject, style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 8),
            Text(
              'From ${m.from}\nTo ${m.to}\n${m.date}',
              style: TextStyle(
                color: Theme.of(context).colorScheme.onSurfaceVariant,
              ),
            ),
            const Divider(height: 24),
            SelectableText(m.body),
          ],
        );
      },
    ),
  );
}
