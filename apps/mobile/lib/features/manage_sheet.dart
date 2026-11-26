import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/api_client.dart';
import '../core/app_models.dart';
import '../core/providers.dart';
import 'provider_widgets.dart';

/// What the Manage sheet needs to know about an app. Provider-engine apps get all of it from their spec.
class ManageConfig {
  const ManageConfig({
    required this.key,
    required this.name,
    required this.color,
    required this.hasWrite,
    required this.writeLabel,
    required this.writeHint,
    required this.writeNeedsSignIn,
    required this.settings,
    required this.onSignInWithWrite,
  });

  factory ManageConfig.fromSpec(
    ProviderSpec s,
    Future<void> Function() signInWithWrite,
  ) => ManageConfig(
    key: s.key,
    name: s.name,
    color: s.color,
    hasWrite: s.hasWrite,
    writeLabel: s.writeLabel,
    writeHint: s.writeHint,
    writeNeedsSignIn: s.writeNeedsSignIn,
    settings: s.settings,
    onSignInWithWrite: signInWithWrite,
  );

  final String key, name, color, writeLabel, writeHint;
  final bool hasWrite, writeNeedsSignIn;
  final List<ProviderSetting> settings;

  /// Starts the provider's sign-in again asking for the extra permission (used when [writeNeedsSignIn]).
  final Future<void> Function() onSignInWithWrite;
}

/// The Manage pop-up, like the web app's: General (full access, dashboard, disconnect) and the app's own settings.
class ManageSheet extends ConsumerStatefulWidget {
  const ManageSheet({
    super.key,
    required this.config,
    required this.status,
    required this.onChanged,
  });
  final ManageConfig config;
  final AppStatus status;
  final VoidCallback onChanged;
  @override
  ConsumerState<ManageSheet> createState() => _ManageSheetState();
}

class _ManageSheetState extends ConsumerState<ManageSheet> {
  Map<String, dynamic>? _settings;
  List<AvailableApp>? _available;
  late bool _write = widget.status.write;
  String? _error, _saved;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final api = ref.read(apiProvider);
    try {
      final r = await Future.wait([
        api.appSettings(widget.config.key),
        api.availableApps(),
      ]);
      if (!mounted) return;
      setState(() {
        _settings = r[0] as Map<String, dynamic>;
        _available = r[1] as List<AvailableApp>;
      });
    } catch (e) {
      if (mounted) setState(() => _error = errorText(e));
    }
  }

  AvailableApp? get _tile {
    final id = widget.status.connectionId;
    if (id == null) return null;
    for (final a in _available ?? const <AvailableApp>[]) {
      if (a.connectionId == id) return a;
    }
    return null;
  }

  Future<void> _run(Future<void> Function() action, {String? done}) async {
    setState(() {
      _busy = true;
      _error = null;
      _saved = null;
    });
    try {
      await action();
      if (done != null && mounted) setState(() => _saved = done);
      widget.onChanged();
    } catch (e) {
      if (mounted) setState(() => _error = errorText(e));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _toggleWrite(bool on) async {
    final c = widget.config;
    if (on && c.writeNeedsSignIn) {
      await _run(
        c.onSignInWithWrite,
        done: 'Finish approving in your browser, then come back.',
      );
      return;
    }
    await _run(() async {
      await ref.read(apiProvider).setAppWrite(c.key, on);
      if (mounted) setState(() => _write = on);
    }, done: on ? 'Full access is on' : 'Back to read-only');
  }

  Future<void> _toggleDashboard(bool on) => _run(
    () async {
      final api = ref.read(apiProvider);
      final tile = _tile;
      if (on) {
        await api.addToDashboard(widget.status.connectionId!);
      } else if (tile?.dashboardId != null) {
        await api.removeFromDashboard(tile!.dashboardId!);
      }
      ref.invalidate(availableAppsProvider);
      ref.invalidate(dashboardAppsProvider);
      _available = await api.availableApps();
    },
    done: on
        ? '${widget.config.name} added to your dashboard'
        : '${widget.config.name} removed from your dashboard',
  );

  Future<void> _save(String key, Object? value) async {
    try {
      final next = await ref.read(apiProvider).updateAppSettings(
        widget.config.key,
        {key: value},
      );
      if (!mounted) return;
      setState(() {
        _settings = next;
        _saved = 'Saved';
        _error = null;
      });
      widget.onChanged();
    } catch (e) {
      if (mounted) setState(() => _error = errorText(e));
    }
  }

  Future<void> _disconnect() async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text('Disconnect ${widget.config.name}?'),
        content: const Text(
          'Daksha forgets the saved sign-in. You can connect again any time.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(c, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(c, true),
            child: const Text('Disconnect'),
          ),
        ],
      ),
    );
    if (ok != true) return;
    await _run(() async {
      await ref.read(apiProvider).disconnectApp(widget.config.key);
      if (mounted) Navigator.pop(context);
    });
  }

  Widget _settingRow(ProviderSetting s) {
    final v = _settings?[s.key];
    switch (s.type) {
      case 'bool':
        return SwitchListTile(
          title: Text(s.label),
          subtitle: s.hint == null ? null : Text(s.hint!),
          value: v == true,
          onChanged: (x) => _save(s.key, x),
        );
      case 'count':
        final n = (v is num) ? v.toInt() : 5;
        return ListTile(
          title: Text(s.label),
          trailing: DropdownButton<int>(
            value: n.clamp(3, 10),
            items: [
              for (final x in const [3, 4, 5, 6, 8, 10])
                DropdownMenuItem(value: x, child: Text('$x')),
            ],
            onChanged: (x) => _save(s.key, x),
          ),
        );
      case 'enum':
        final options = s.options ?? const <String>[];
        return ListTile(
          title: Text(s.label),
          subtitle: s.hint == null ? null : Text(s.hint!),
          trailing: DropdownButton<String>(
            value: options.contains(v) ? v as String : null,
            hint: const Text('Choose'),
            items: [
              for (final o in options)
                DropdownMenuItem(value: o, child: Text(o)),
            ],
            onChanged: (x) => _save(s.key, x),
          ),
        );
      default:
        return _TextSetting(
          key: ValueKey('${s.key}:$v'),
          setting: s,
          value: (v ?? '').toString(),
          onSave: (x) => _save(s.key, x),
        );
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.config;
    final scheme = Theme.of(context).colorScheme;
    final extra = c.settings;
    return DefaultTabController(
      length: extra.isEmpty ? 1 : 2,
      child: SafeArea(
        child: ConstrainedBox(
          constraints: BoxConstraints(
            maxHeight: MediaQuery.of(context).size.height * 0.85,
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(20, 0, 20, 8),
                child: Row(
                  children: [
                    AppBadge(name: c.name, color: c.color, size: 44),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            c.name,
                            style: Theme.of(context).textTheme.titleLarge,
                          ),
                          Text(
                            'Connected as ${widget.status.accountName ?? 'you'}',
                            style: TextStyle(color: scheme.onSurfaceVariant),
                            overflow: TextOverflow.ellipsis,
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
              TabBar(
                tabs: [
                  const Tab(text: 'General'),
                  if (extra.isNotEmpty) const Tab(text: 'Settings'),
                ],
              ),
              if (_error != null)
                Container(
                  width: double.infinity,
                  margin: const EdgeInsets.fromLTRB(16, 8, 16, 0),
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: scheme.errorContainer,
                    borderRadius: BorderRadius.circular(14),
                  ),
                  child: Text(
                    _error!,
                    style: TextStyle(color: scheme.onErrorContainer),
                  ),
                ),
              if (_saved != null)
                Container(
                  width: double.infinity,
                  margin: const EdgeInsets.fromLTRB(16, 8, 16, 0),
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: const Color(0xFFC8F169),
                    borderRadius: BorderRadius.circular(14),
                  ),
                  child: Text(
                    _saved!,
                    style: const TextStyle(color: Color(0xFF17181C)),
                  ),
                ),
              Flexible(
                child: TabBarView(
                  children: [
                    ListView(
                      padding: const EdgeInsets.all(8),
                      children: [
                        if (c.hasWrite)
                          SwitchListTile(
                            title: Text(c.writeLabel),
                            subtitle: Text(
                              '${c.writeHint}${c.writeNeedsSignIn ? ' ${c.name} asks you to approve this.' : ''}',
                            ),
                            value: _write,
                            onChanged: _busy ? null : _toggleWrite,
                          )
                        else
                          ListTile(
                            leading: const Icon(Icons.visibility_outlined),
                            title: Text('${c.name} is read-only'),
                            subtitle: const Text(
                              'Daksha looks things up and never changes anything.',
                            ),
                          ),
                        SwitchListTile(
                          title: Text('Show ${c.name} on dashboard'),
                          value: _tile?.dashboardId != null,
                          onChanged: _busy || _available == null
                              ? null
                              : _toggleDashboard,
                        ),
                        const SizedBox(height: 8),
                        Padding(
                          padding: const EdgeInsets.symmetric(horizontal: 16),
                          child: OutlinedButton.icon(
                            onPressed: _busy ? null : _disconnect,
                            icon: Icon(Icons.link_off, color: scheme.error),
                            label: Text(
                              'Disconnect',
                              style: TextStyle(color: scheme.error),
                            ),
                          ),
                        ),
                      ],
                    ),
                    if (extra.isNotEmpty)
                      _settings == null
                          ? const Center(child: CircularProgressIndicator())
                          : ListView(
                              padding: const EdgeInsets.all(8),
                              children: [for (final s in extra) _settingRow(s)],
                            ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _TextSetting extends StatefulWidget {
  const _TextSetting({
    super.key,
    required this.setting,
    required this.value,
    required this.onSave,
  });
  final ProviderSetting setting;
  final String value;
  final ValueChanged<String> onSave;
  @override
  State<_TextSetting> createState() => _TextSettingState();
}

class _TextSettingState extends State<_TextSetting> {
  late final _c = TextEditingController(text: widget.value);
  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Expanded(
          child: TextField(
            controller: _c,
            decoration: InputDecoration(
              labelText: widget.setting.label,
              hintText: widget.setting.placeholder,
              helperText: widget.setting.hint,
              helperMaxLines: 3,
            ),
          ),
        ),
        const SizedBox(width: 8),
        Padding(
          padding: const EdgeInsets.only(top: 6),
          child: FilledButton(
            onPressed: () => widget.onSave(_c.text.trim()),
            child: const Text('Save'),
          ),
        ),
      ],
    ),
  );
}
