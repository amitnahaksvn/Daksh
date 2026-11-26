import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../core/app_models.dart';
import '../core/links.dart';
import '../core/providers.dart';
import '../core/widgets.dart';
import 'provider_widgets.dart';

/// Apps that sign in once and share the common status / settings / dashboard endpoints (their own screens have more on the web).
const oneAppKeys = {'github', 'notion', 'discord', 'spotify'};

/// Apps with a place in the mobile app already.
const _inApp = {
  'whatsapp': '/chats',
  'telegram': '/chats',
  'gmail': '/email',
  'google-calendar': '/calendar',
};

/// Every app, like the web's Apps page: live ones first, then each category. Tapping opens the app (or its sign-in).
class AppsScreen extends ConsumerStatefulWidget {
  const AppsScreen({super.key});
  @override
  ConsumerState<AppsScreen> createState() => _AppsScreenState();
}

class _AppsScreenState extends ConsumerState<AppsScreen> {
  String _query = '';
  String _category = 'All';

  @override
  Widget build(BuildContext context) {
    final catalog = ref.watch(appCatalogProvider);
    final statuses =
        ref.watch(providerStatusesProvider).value ?? const <String, bool>{};
    return AsyncBody<List<AppCatalogEntry>>(
      value: catalog,
      onRetry: () => ref.invalidate(appCatalogProvider),
      builder: (apps) {
        final categories = [
          'All',
          ...{for (final a in apps) a.category},
        ];
        final shown =
            apps
                .where(
                  (a) =>
                      (_category == 'All' || a.category == _category) &&
                      (_query.isEmpty ||
                          a.name.toLowerCase().contains(_query.toLowerCase())),
                )
                .toList()
              ..sort(
                (a, b) =>
                    (statuses[b.key] == true ? 1 : 0) -
                            (statuses[a.key] == true ? 1 : 0) !=
                        0
                    ? (statuses[b.key] == true ? 1 : 0) -
                          (statuses[a.key] == true ? 1 : 0)
                    : (b.isEnabled ? 1 : 0) - (a.isEnabled ? 1 : 0),
              );
        final live = apps.where((a) => a.isEnabled).length;
        return Column(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(12, 4, 12, 0),
              child: TextField(
                decoration: const InputDecoration(
                  prefixIcon: Icon(Icons.search),
                  hintText: 'Search apps',
                ),
                onChanged: (v) => setState(() => _query = v),
              ),
            ),
            SizedBox(
              height: 52,
              child: ListView(
                scrollDirection: Axis.horizontal,
                padding: const EdgeInsets.symmetric(
                  horizontal: 12,
                  vertical: 8,
                ),
                children: [
                  for (final c in categories)
                    Padding(
                      padding: const EdgeInsets.only(right: 8),
                      child: ChoiceChip(
                        label: Text(c),
                        selected: c == _category,
                        onSelected: (_) => setState(() => _category = c),
                      ),
                    ),
                ],
              ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 4),
              child: Align(
                alignment: Alignment.centerLeft,
                child: Text(
                  '$live of ${apps.length} apps work today',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            ),
            Expanded(
              child: RefreshIndicator(
                onRefresh: () async {
                  ref.invalidate(appCatalogProvider);
                  ref.invalidate(providerStatusesProvider);
                },
                child: ListView.builder(
                  padding: const EdgeInsets.fromLTRB(12, 0, 12, 16),
                  itemCount: shown.length,
                  itemBuilder: (context, i) => _AppRow(
                    app: shown[i],
                    providerConnected: statuses[shown[i].key],
                    isProvider: statuses.containsKey(shown[i].key),
                  ),
                ),
              ),
            ),
          ],
        );
      },
    );
  }
}

class _AppRow extends ConsumerWidget {
  const _AppRow({
    required this.app,
    required this.providerConnected,
    required this.isProvider,
  });
  final AppCatalogEntry app;
  final bool? providerConnected;
  final bool isProvider;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final isOne = oneAppKeys.contains(app.key);
    final oneStatus = isOne
        ? ref.watch(appStatusProvider(app.key)).value
        : null;
    final connected = isProvider
        ? providerConnected == true
        : isOne
        ? oneStatus?.connected == true
        : null;
    final live = app.isEnabled;
    final label = !live
        ? 'Coming soon'
        : connected == null
        ? 'Open'
        : connected
        ? 'Connected'
        : 'Connect';

    void open() {
      if (isProvider) {
        context.push('/apps/provider/${app.key}');
      } else if (isOne) {
        context.push('/apps/one/${app.key}');
      } else if (_inApp.containsKey(app.key)) {
        context.push(_inApp[app.key]!);
      } else if (live) {
        ref.read(linkOpenerProvider)('$webUrl/apps/${app.key}');
      } else {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('${app.name} is not available yet.')),
        );
      }
    }

    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Tile(
        child: ListTile(
          leading: AppBadge(name: app.name, color: app.color),
          title: Text(
            app.name,
            style: const TextStyle(fontWeight: FontWeight.w700),
          ),
          subtitle: Text(
            app.description.isEmpty ? app.category : app.description,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
          trailing: StatusChip(label, lime: connected == true),
          onTap: open,
        ),
      ),
    );
  }
}
