import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/app_models.dart';
import '../core/providers.dart';
import '../core/widgets.dart';
import 'manage_sheet.dart';
import 'provider_widgets.dart';

/// One screen for every provider-engine app (YouTube, Instagram, Shopify, Home Assistant ...): it draws itself from the app's server-side spec.
class ProviderAppScreen extends ConsumerWidget {
  const ProviderAppScreen({super.key, required this.appKey});
  final String appKey;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final spec = ref.watch(providerSpecProvider(appKey));
    return spec.when(
      loading: () => Scaffold(
        appBar: AppBar(title: const Text('Apps')),
        body: const Center(child: CircularProgressIndicator()),
      ),
      error: (e, _) => Scaffold(
        appBar: AppBar(title: const Text('Apps')),
        body: AsyncBody<void>(
          value: AsyncError(e, StackTrace.empty),
          onRetry: () => ref.invalidate(providerSpecProvider(appKey)),
          builder: (_) => const SizedBox(),
        ),
      ),
      data: (s) => _Body(spec: s),
    );
  }
}

class _Body extends ConsumerStatefulWidget {
  const _Body({required this.spec});
  final ProviderSpec spec;
  @override
  ConsumerState<_Body> createState() => _BodyState();
}

class _BodyState extends ConsumerState<_Body> with WidgetsBindingObserver {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  // Coming back from the browser after signing in: check whether it worked.
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) _refresh();
  }

  void _refresh() {
    ref.invalidate(appStatusProvider(widget.spec.key));
    ref.invalidate(providerStatusesProvider);
  }

  Future<void> _startSignIn({
    required bool write,
    Map<String, String> inputs = const {},
  }) async {
    final api = ref.read(apiProvider);
    final url = await api.oauthUrl(widget.spec.key, {
      'write': '$write',
      ...inputs,
    });
    final ok = await ref.read(linkOpenerProvider)(url);
    if (!ok) throw Exception('Could not open the browser to sign in.');
  }

  void _manage(AppStatus status) {
    final spec = widget.spec;
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (_) => ManageSheet(
        config: ManageConfig.fromSpec(spec, () => _startSignIn(write: true)),
        status: status,
        onChanged: _refresh,
      ),
    ).then((_) => _refresh());
  }

  @override
  Widget build(BuildContext context) {
    final spec = widget.spec;
    final status = ref.watch(appStatusProvider(spec.key));
    return Scaffold(
      appBar: AppBar(
        title: Text(spec.name),
        actions: [
          if (status.value?.connected == true)
            IconButton(
              tooltip: 'Manage',
              icon: const Icon(Icons.tune),
              onPressed: () => _manage(status.value!),
            ),
        ],
      ),
      body: AsyncBody<AppStatus>(
        value: status,
        onRetry: _refresh,
        builder: (st) => st.connected
            ? _Connected(spec: spec, status: st)
            : ListView(
                padding: const EdgeInsets.all(12),
                children: [
                  ConnectCard(
                    spec: spec,
                    status: st,
                    onSignIn: _startSignIn,
                    onConnected: _refresh,
                  ),
                ],
              ),
      ),
    );
  }
}

/// The sign-in card: the details the app needs, then a button (the browser sign-in, a credential check, or turning it on).
class ConnectCard extends ConsumerStatefulWidget {
  const ConnectCard({
    super.key,
    required this.spec,
    required this.status,
    required this.onSignIn,
    required this.onConnected,
  });
  final ProviderSpec spec;
  final AppStatus status;
  final Future<void> Function({required bool write, Map<String, String> inputs})
  onSignIn;
  final VoidCallback onConnected;
  @override
  ConsumerState<ConnectCard> createState() => _ConnectCardState();
}

class _ConnectCardState extends ConsumerState<ConnectCard> {
  late final Map<String, String> _values = defaultsFor(
    widget.spec.connectFields,
  );
  bool _full = false, _busy = false, _waiting = false;
  String? _error;

  Future<void> _connect() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    final spec = widget.spec;
    try {
      if (spec.isOAuth) {
        await widget.onSignIn(write: _full, inputs: _values);
        if (mounted) setState(() => _waiting = true);
      } else {
        await ref.read(apiProvider).connectWithDetails(spec.key, _values);
        widget.onConnected();
      }
    } catch (e) {
      if (mounted) setState(() => _error = errorText(e));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final spec = widget.spec;
    final scheme = Theme.of(context).colorScheme;
    final notSetUp = !widget.status.configured && spec.auth != 'credentials';
    return Tile(
      padding: const EdgeInsets.all(20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              AppBadge(name: spec.name, color: spec.color),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                  spec.connectTitle,
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Text(
            spec.connectBlurb,
            style: TextStyle(color: scheme.onSurfaceVariant),
          ),
          if (notSetUp)
            Container(
              margin: const EdgeInsets.only(top: 12),
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: scheme.surfaceContainer,
                borderRadius: BorderRadius.circular(12),
              ),
              child: Text(
                '${spec.name} is not set up on the server yet. An administrator needs to add its keys (see ${spec.setupDoc}).',
              ),
            ),
          for (final f in spec.connectFields)
            Padding(
              padding: const EdgeInsets.only(top: 12),
              child: InputField(
                appKey: spec.key,
                input: f,
                value: _values[f.key] ?? '',
                onChanged: (v) => _values[f.key] = v,
              ),
            ),
          if (spec.isOAuth && spec.hasWrite)
            CheckboxListTile(
              contentPadding: EdgeInsets.zero,
              controlAffinity: ListTileControlAffinity.leading,
              value: _full,
              onChanged: (v) => setState(() => _full = v == true),
              title: Text(spec.writeLabel),
              subtitle: Text(
                '${spec.writeHint} Off by default; you can change it later in Manage.',
              ),
            ),
          if (_error != null)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(_error!, style: TextStyle(color: scheme.error)),
            ),
          if (_waiting)
            Padding(
              padding: const EdgeInsets.only(top: 12),
              child: Text(
                'Finish signing in in your browser, then come back here.',
                style: TextStyle(
                  color: scheme.primary,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ),
          const SizedBox(height: 14),
          Row(
            children: [
              FilledButton(
                onPressed: _busy ? null : _connect,
                child: Text(_busy ? 'Working…' : spec.connectLabel),
              ),
              if (_waiting) ...[
                const SizedBox(width: 8),
                TextButton(
                  onPressed: widget.onConnected,
                  child: const Text("I've finished"),
                ),
              ],
            ],
          ),
        ],
      ),
    );
  }
}

class _Connected extends StatelessWidget {
  const _Connected({required this.spec, required this.status});
  final ProviderSpec spec;
  final AppStatus status;

  @override
  Widget build(BuildContext context) {
    final tabs = [
      ...spec.tabs.map((v) => v.label),
      if (spec.actions.isNotEmpty) 'Do something',
    ];
    return DefaultTabController(
      length: tabs.length,
      child: Column(
        children: [
          Align(
            alignment: Alignment.centerLeft,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 4),
              child: Text(
                '${status.accountName ?? ''}${status.write ? '' : ' · read-only'}',
                style: TextStyle(
                  color: Theme.of(context).colorScheme.onSurfaceVariant,
                ),
              ),
            ),
          ),
          TabBar(
            isScrollable: true,
            tabAlignment: TabAlignment.start,
            tabs: [for (final t in tabs) Tab(text: t)],
          ),
          Expanded(
            child: TabBarView(
              children: [
                for (final v in spec.tabs)
                  ViewTab(
                    key: ValueKey('${spec.key}:${v.id}'),
                    appKey: spec.key,
                    view: v,
                  ),
                if (spec.actions.isNotEmpty)
                  ActionsTab(
                    appKey: spec.key,
                    actions: spec.actions,
                    canWrite: status.write,
                  ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// One list of an app, with its search or filter fields when it has any.
class ViewTab extends ConsumerStatefulWidget {
  const ViewTab({super.key, required this.appKey, required this.view});
  final String appKey;
  final ProviderView view;
  @override
  ConsumerState<ViewTab> createState() => _ViewTabState();
}

class _ViewTabState extends ConsumerState<ViewTab>
    with AutomaticKeepAliveClientMixin {
  late final Map<String, String> _values = defaultsFor(widget.view.inputs);
  Future<List<ProviderItem>>? _future;

  @override
  bool get wantKeepAlive => true;

  @override
  void initState() {
    super.initState();
    if (!widget.view.needsInput) _future = _fetch();
  }

  Future<List<ProviderItem>> _fetch() => ref.read(apiProvider).providerView(widget.appKey, widget.view.id, Map.of(_values));

  void _load() {
    final next = _fetch();
    setState(() {
      _future = next;
    });
  }

  void _drill(ProviderItem item) {
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => DrillPage(appKey: widget.appKey, from: item),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    super.build(context);
    final v = widget.view;
    return RefreshIndicator(
      onRefresh: () async => _future == null ? null : _load(),
      child: ListView(
        padding: const EdgeInsets.all(12),
        children: [
          if (v.inputs.isNotEmpty)
            Tile(
              padding: const EdgeInsets.all(16),
              child: Column(
                children: [
                  for (final i in v.inputs)
                    Padding(
                      padding: const EdgeInsets.only(bottom: 10),
                      child: InputField(
                        appKey: widget.appKey,
                        input: i,
                        value: _values[i.key] ?? '',
                        onChanged: (x) => _values[i.key] = x,
                      ),
                    ),
                  Align(
                    alignment: Alignment.centerRight,
                    child: FilledButton(
                      onPressed: () => missingRequired(v.inputs, _values)
                          ? ScaffoldMessenger.of(context).showSnackBar(
                              const SnackBar(
                                content: Text('Fill in the details first.'),
                              ),
                            )
                          : _load(),
                      child: Text(v.needsInput ? 'Show' : 'Update'),
                    ),
                  ),
                ],
              ),
            ),
          const SizedBox(height: 8),
          if (_future == null)
            const Padding(
              padding: EdgeInsets.all(24),
              child: Center(
                child: Text('Fill in the details above to see results.'),
              ),
            )
          else
            FutureBuilder<List<ProviderItem>>(
              future: _future,
              builder: (context, snap) {
                if (snap.connectionState != ConnectionState.done)
                  return const Padding(
                    padding: EdgeInsets.all(24),
                    child: Center(child: CircularProgressIndicator()),
                  );
                if (snap.hasError) {
                  return Column(
                    children: [
                      Text(
                        errorText(snap.error!),
                        textAlign: TextAlign.center,
                        style: TextStyle(
                          color: Theme.of(context).colorScheme.error,
                        ),
                      ),
                      TextButton(
                        onPressed: _load,
                        child: const Text('Try again'),
                      ),
                    ],
                  );
                }
                return ItemList(
                  items: snap.data!,
                  empty: v.empty ?? 'Nothing to show.',
                  onDrill: _drill,
                );
              },
            ),
        ],
      ),
    );
  }
}

/// The inside of a row (a playlist's videos, a post's comments, an email's text).
class DrillPage extends ConsumerWidget {
  const DrillPage({super.key, required this.appKey, required this.from});
  final String appKey;
  final ProviderItem from;

  @override
  Widget build(BuildContext context, WidgetRef ref) => Scaffold(
    appBar: AppBar(title: Text(from.title, overflow: TextOverflow.ellipsis)),
    body: FutureBuilder<List<ProviderItem>>(
      future: ref.read(apiProvider).providerView(appKey, from.drill!, {
        'arg': from.id,
      }),
      builder: (context, snap) {
        if (snap.connectionState != ConnectionState.done)
          return const Center(child: CircularProgressIndicator());
        if (snap.hasError)
          return Center(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Text(errorText(snap.error!), textAlign: TextAlign.center),
            ),
          );
        return ListView(
          padding: const EdgeInsets.all(12),
          children: [ItemList(items: snap.data!, empty: 'Nothing here.')],
        );
      },
    ),
  );
}

class ActionsTab extends StatelessWidget {
  const ActionsTab({
    super.key,
    required this.appKey,
    required this.actions,
    required this.canWrite,
  });
  final String appKey;
  final List<ProviderAction> actions;
  final bool canWrite;

  @override
  Widget build(BuildContext context) => ListView(
    padding: const EdgeInsets.all(12),
    children: [
      if (!canWrite)
        Padding(
          padding: const EdgeInsets.only(bottom: 10),
          child: Text(
            'Daksha has read-only access. Turn on full access in Manage to do these.',
            style: TextStyle(
              color: Theme.of(context).colorScheme.onSurfaceVariant,
            ),
          ),
        ),
      for (final a in actions)
        Padding(
          padding: const EdgeInsets.only(bottom: 10),
          child: ActionCard(appKey: appKey, action: a, canWrite: canWrite),
        ),
    ],
  );
}

class ActionCard extends ConsumerStatefulWidget {
  const ActionCard({
    super.key,
    required this.appKey,
    required this.action,
    required this.canWrite,
  });
  final String appKey;
  final ProviderAction action;
  final bool canWrite;
  @override
  ConsumerState<ActionCard> createState() => _ActionCardState();
}

class _ActionCardState extends ConsumerState<ActionCard> {
  late final Map<String, String> _values = defaultsFor(widget.action.inputs);
  bool _busy = false;
  String? _error;
  ProviderActionResult? _done;

  Future<void> _run() async {
    if (missingRequired(widget.action.inputs, _values)) {
      setState(() => _error = 'Fill in the required details first.');
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
      _done = null;
    });
    try {
      final r = await ref
          .read(apiProvider)
          .providerAction(widget.appKey, widget.action.id, Map.of(_values));
      if (mounted) setState(() => _done = r);
    } catch (e) {
      if (mounted) setState(() => _error = errorText(e));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final a = widget.action;
    final scheme = Theme.of(context).colorScheme;
    return Tile(
      child: ExpansionTile(
        title: Text(
          a.label,
          style: const TextStyle(fontWeight: FontWeight.w700),
        ),
        subtitle: a.hint == null ? null : Text(a.hint!),
        childrenPadding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
        shape: const Border(),
        collapsedShape: const Border(),
        children: [
          for (final i in a.inputs)
            Padding(
              padding: const EdgeInsets.only(bottom: 10),
              child: InputField(
                appKey: widget.appKey,
                input: i,
                value: _values[i.key] ?? '',
                onChanged: (x) => _values[i.key] = x,
              ),
            ),
          if (_error != null)
            Align(
              alignment: Alignment.centerLeft,
              child: Text(_error!, style: TextStyle(color: scheme.error)),
            ),
          if (_done != null)
            Align(
              alignment: Alignment.centerLeft,
              child: Row(
                children: [
                  Flexible(child: Text(_done!.message)),
                  if (_done!.url != null)
                    TextButton(
                      onPressed: () =>
                          ref.read(linkOpenerProvider)(_done!.url!),
                      child: const Text('Open'),
                    ),
                ],
              ),
            ),
          Align(
            alignment: Alignment.centerLeft,
            child: FilledButton(
              onPressed: widget.canWrite && !_busy ? _run : null,
              child: Text(_busy ? 'Working…' : a.label),
            ),
          ),
        ],
      ),
    );
  }
}
