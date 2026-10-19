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
        builder: (items) => ListView(children: [
          for (final i in items) ListTile(title: Text('${i.key} — ${i.state}'), subtitle: Text(i.note)),
        ]),
      );
}
