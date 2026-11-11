import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/providers.dart';
import '../core/widgets.dart';

class IntegrationsScreen extends ConsumerWidget {
  const IntegrationsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => AsyncBody(
        value: ref.watch(integrationsProvider),
        onRetry: () => ref.invalidate(integrationsProvider),
        builder: (items) => ListView(padding: const EdgeInsets.fromLTRB(12, 4, 12, 16), children: [
          for (final i in items)
            Padding(
              padding: const EdgeInsets.only(bottom: 10),
              child: Tile(
                padding: const EdgeInsets.all(16),
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Row(children: [
                    Expanded(child: Text(i.key, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 16))),
                    StatusChip(i.state, lime: i.state != 'Mock'),
                  ]),
                  const SizedBox(height: 6),
                  Text(i.note, style: TextStyle(color: Theme.of(context).colorScheme.onSurfaceVariant)),
                ]),
              ),
            ),
        ]),
      );
}
