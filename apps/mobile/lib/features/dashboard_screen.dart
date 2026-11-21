import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/api_client.dart';
import '../core/providers.dart';
import '../core/widgets.dart';

/// Apps whose chats live in the Chats tab, so they are not tiles.
const _messaging = {'whatsapp', 'telegram'};

/// The dashboard: two tabs, Command Centre and App tiles.
class DashboardScreen extends StatelessWidget {
  const DashboardScreen({super.key});

  @override
  Widget build(BuildContext context) => DefaultTabController(
        length: 2,
        child: Column(children: [
          // Connected folder tabs, like the website: the active tab joins the content below and carries an accent line on top.
          Container(
            margin: const EdgeInsets.fromLTRB(12, 4, 12, 0),
            decoration: BoxDecoration(
              color: Theme.of(context).colorScheme.surfaceContainer,
              border: Border.all(color: Theme.of(context).colorScheme.outlineVariant),
              borderRadius: const BorderRadius.vertical(top: Radius.circular(16)),
            ),
            child: TabBar(
              dividerColor: Colors.transparent,
              indicatorSize: TabBarIndicatorSize.tab,
              indicator: BoxDecoration(
                color: Theme.of(context).colorScheme.surface,
                border: Border(top: BorderSide(color: Theme.of(context).colorScheme.primary, width: 3)),
                borderRadius: const BorderRadius.vertical(top: Radius.circular(14)),
              ),
              labelColor: Theme.of(context).colorScheme.onSurface,
              unselectedLabelColor: Theme.of(context).colorScheme.onSurfaceVariant,
              labelStyle: const TextStyle(fontWeight: FontWeight.w800),
              tabs: const [Tab(text: 'Command Centre'), Tab(text: 'App tiles')],
            ),
          ),
          const Expanded(child: TabBarView(children: [_CommandCentre(), _AppTiles()])),
        ]),
      );
}

class _CommandCentre extends ConsumerWidget {
  const _CommandCentre();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final apps = ref.watch(dashboardAppsProvider);
    final tasks = ref.watch(tasksProvider);
    return RefreshIndicator(
      onRefresh: () async {
        ref.invalidate(dashboardAppsProvider);
        ref.invalidate(tasksProvider);
      },
      child: ListView(padding: const EdgeInsets.all(12), children: [
        Tile(
          padding: const EdgeInsets.all(16),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text('Unread across your apps', style: Theme.of(context).textTheme.labelLarge),
            const SizedBox(height: 6),
            apps.when(
              loading: () => const LinearProgressIndicator(),
              error: (e, _) => Text(e.toString()),
              data: (list) {
                final total = list.fold<int>(0, (n, a) => n + a.unread);
                return Text(total == 0 ? 'Nothing unread' : '$total unread', style: Theme.of(context).textTheme.headlineMedium);
              },
            ),
          ]),
        ),
        const SizedBox(height: 8),
        Tile(
          padding: const EdgeInsets.all(16),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text('Needs you', style: Theme.of(context).textTheme.labelLarge),
            const SizedBox(height: 6),
            tasks.when(
              loading: () => const LinearProgressIndicator(),
              error: (e, _) => Text(e.toString()),
              data: (list) {
                final open = list.where((t) => !t.completed).toList();
                if (open.isEmpty) return const Text('Nothing needs you right now.');
                return Column(children: [
                  for (final t in open.take(6)) ListTile(dense: true, contentPadding: EdgeInsets.zero, leading: const Icon(Icons.radio_button_unchecked, size: 20), title: Text(t.title)),
                ]);
              },
            ),
          ]),
        ),
      ]),
    );
  }
}

class _AppTiles extends ConsumerWidget {
  const _AppTiles();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final apps = ref.watch(dashboardAppsProvider);
    return Column(children: [
      Align(
        alignment: Alignment.centerRight,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
          child: OutlinedButton.icon(onPressed: () => _chooseApps(context, ref), icon: const Icon(Icons.tune, size: 18), label: const Text('Choose apps')),
        ),
      ),
      Expanded(
        child: AsyncBody(
          value: apps,
          onRetry: () => ref.invalidate(dashboardAppsProvider),
          builder: (list) {
            final tiles = list.where((a) => !_messaging.contains(a.key)).toList();
            return RefreshIndicator(
              onRefresh: () async => ref.invalidate(dashboardAppsProvider),
              child: ListView(padding: const EdgeInsets.all(12), children: [
                if (tiles.isEmpty)
                  const Padding(padding: EdgeInsets.all(24), child: Text('No app tiles yet. Use Choose apps to add a connected app.', textAlign: TextAlign.center)),
                for (final a in tiles) Padding(padding: const EdgeInsets.only(bottom: 10), child: a.key == 'gmail' ? _GmailTile(app: a) : _AppTile(app: a)),
              ]),
            );
          },
        ),
      ),
    ]);
  }

  Future<void> _chooseApps(BuildContext context, WidgetRef ref) async {
    ref.invalidate(availableAppsProvider);
    final changed = await showModalBottomSheet<bool>(context: context, isScrollControlled: true, showDragHandle: true, builder: (_) => const _ChooseAppsSheet());
    if (changed == true) ref.invalidate(dashboardAppsProvider);
  }
}

class _AppTile extends StatelessWidget {
  const _AppTile({required this.app});
  final DashboardApp app;

  @override
  Widget build(BuildContext context) => Tile(
        padding: const EdgeInsets.all(16),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Row(children: [
            Expanded(child: Text(app.name, style: Theme.of(context).textTheme.titleMedium)),
            StatusChip(app.status, lime: app.status == 'Connected'),
          ]),
          if (app.account != null) Text(app.account!, style: Theme.of(context).textTheme.bodySmall),
          if (app.unread > 0) Padding(padding: const EdgeInsets.only(top: 6), child: Text('${app.unread} unread')),
          if (app.lastPreview != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text(app.lastPreview!, maxLines: 2, overflow: TextOverflow.ellipsis)),
        ]),
      );
}

/// One Gmail account: its own numbers and priority emails.
class _GmailTile extends ConsumerWidget {
  const _GmailTile({required this.app});
  final DashboardApp app;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final summary = ref.watch(gmailSummaryProvider(app.connectionId));
    return Tile(
      padding: const EdgeInsets.all(16),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          const Icon(Icons.mail_outline),
          const SizedBox(width: 8),
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text('Gmail', style: Theme.of(context).textTheme.titleMedium),
            if (app.account != null) Text(app.account!, style: Theme.of(context).textTheme.bodySmall, overflow: TextOverflow.ellipsis),
          ])),
          IconButton(tooltip: 'Refresh', icon: const Icon(Icons.refresh), onPressed: () => ref.invalidate(gmailSummaryProvider(app.connectionId))),
        ]),
        const SizedBox(height: 8),
        summary.when(
          loading: () => const Padding(padding: EdgeInsets.symmetric(vertical: 12), child: LinearProgressIndicator()),
          error: (e, _) => Text(e.toString(), style: TextStyle(color: Theme.of(context).colorScheme.error)),
          data: (s) => Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              _Stat('Unread', s.unread), _Stat('Today', s.today), _Stat('Important', s.important),
            ]),
            if (s.syncError != null) Padding(padding: const EdgeInsets.only(top: 8), child: Text('Sync problem: ${s.syncError}', style: TextStyle(color: Theme.of(context).colorScheme.error, fontSize: 12))),
            if (s.priority.isNotEmpty) ...[
              const SizedBox(height: 10),
              Text('Priority emails', style: Theme.of(context).textTheme.labelLarge),
              for (final p in s.priority.take(5))
                ListTile(
                  dense: true, contentPadding: EdgeInsets.zero,
                  title: Text(p.subject, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontWeight: p.unread ? FontWeight.w700 : FontWeight.w400)),
                  subtitle: Text(p.from, maxLines: 1, overflow: TextOverflow.ellipsis),
                  trailing: StatusChip(p.label),
                ),
            ],
          ]),
        ),
      ]),
    );
  }
}

class _Stat extends StatelessWidget {
  const _Stat(this.label, this.value);
  final String label;
  final int value;
  @override
  Widget build(BuildContext context) => Expanded(
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text('$value', style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700)),
          Text(label, style: Theme.of(context).textTheme.bodySmall),
        ]),
      );
}

/// The Choose apps pop-up: every connected app and account, ticked when it is already on the dashboard.
class _ChooseAppsSheet extends ConsumerStatefulWidget {
  const _ChooseAppsSheet();
  @override
  ConsumerState<_ChooseAppsSheet> createState() => _ChooseAppsSheetState();
}

class _ChooseAppsSheetState extends ConsumerState<_ChooseAppsSheet> {
  Set<String>? _ticked;
  bool _saving = false;
  String? _error;

  Future<void> _save(List<AvailableApp> items) async {
    setState(() { _saving = true; _error = null; });
    final api = ref.read(apiProvider);
    try {
      for (final a in items) {
        final on = _ticked!.contains(a.connectionId);
        if (on && a.dashboardId == null) await api.addToDashboard(a.connectionId);
        if (!on && a.dashboardId != null) await api.removeFromDashboard(a.dashboardId!);
      }
      if (mounted) Navigator.pop(context, true);
    } catch (e) {
      if (mounted) setState(() { _saving = false; _error = 'Could not save your choices. Try again.'; });
    }
  }

  @override
  Widget build(BuildContext context) {
    final available = ref.watch(availableAppsProvider);
    return SafeArea(
      child: ConstrainedBox(
        constraints: BoxConstraints(maxHeight: MediaQuery.of(context).size.height * 0.75),
        child: available.when(
          loading: () => const Padding(padding: EdgeInsets.all(32), child: Center(child: CircularProgressIndicator())),
          error: (e, _) => Padding(padding: const EdgeInsets.all(24), child: Text(e.toString())),
          data: (items) {
            _ticked ??= {for (final a in items.where((a) => a.dashboardId != null)) a.connectionId};
            return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(20, 0, 20, 8),
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text('Apps on your dashboard', style: Theme.of(context).textTheme.titleLarge),
                  const Text('Tick the connected apps and accounts you want as tiles.'),
                ]),
              ),
              Flexible(
                child: items.isEmpty
                    ? const Padding(padding: EdgeInsets.all(24), child: Text('No connected apps yet. Connect one from the web app.'))
                    : ListView(shrinkWrap: true, children: [
                        for (final a in items)
                          CheckboxListTile(
                            value: _ticked!.contains(a.connectionId),
                            onChanged: (v) => setState(() => v == true ? _ticked!.add(a.connectionId) : _ticked!.remove(a.connectionId)),
                            title: Text(a.name),
                            subtitle: a.account != null ? Text(a.account!) : null,
                          ),
                      ]),
              ),
              if (_error != null) Padding(padding: const EdgeInsets.symmetric(horizontal: 20), child: Text(_error!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
              Padding(
                padding: const EdgeInsets.all(16),
                child: Row(mainAxisAlignment: MainAxisAlignment.end, children: [
                  TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancel')),
                  const SizedBox(width: 8),
                  FilledButton(onPressed: _saving || items.isEmpty ? null : () => _save(items), child: Text(_saving ? 'Saving…' : 'Save')),
                ]),
              ),
            ]);
          },
        ),
      ),
    );
  }
}
