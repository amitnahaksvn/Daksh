import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/api_client.dart';
import '../core/providers.dart';
import '../core/widgets.dart';

/// Privacy and permissions. Platform limits (microphone in background, cellular calls,
/// third-party messaging) are documented in docs/MOBILE_PLATFORM_LIMITS.md.
class PrivacyScreen extends ConsumerWidget {
  const PrivacyScreen({super.key});

  Future<void> _save(BuildContext context, WidgetRef ref, PrivacySettings s) async {
    try {
      await ref.read(apiProvider).updatePrivacy(s);
      ref.invalidate(privacyProvider);
    } catch (e) {
      if (context.mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.toString())));
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) => AsyncBody(
        value: ref.watch(privacyProvider),
        onRetry: () => ref.invalidate(privacyProvider),
        builder: (s) => ListView(children: [
          SwitchListTile(
            title: const Text('Remember things between conversations'),
            value: s.memoryEnabled,
            onChanged: (v) => _save(context, ref, PrivacySettings(v, s.allowProviderDataSharing, s.retentionDays)),
          ),
          SwitchListTile(
            title: const Text('Allow sharing content with AI providers'),
            value: s.allowProviderDataSharing,
            onChanged: (v) => _save(context, ref, PrivacySettings(s.memoryEnabled, v, s.retentionDays)),
          ),
          ListTile(title: const Text('Retention'), subtitle: Text('${s.retentionDays} days')),
        ]),
      );
}
