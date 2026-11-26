import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/app_models.dart';
import '../core/providers.dart';
import '../core/widgets.dart';
import 'manage_sheet.dart';
import 'provider_widgets.dart';

class _OneApp {
  const _OneApp(
    this.name,
    this.color,
    this.blurb,
    this.writeLabel,
    this.writeHint,
    this.writeNeedsSignIn, {
    this.tokenLabel,
  });
  final String name, color, blurb, writeLabel, writeHint;
  final bool writeNeedsSignIn;

  /// Discord asks for a bot token instead of a browser sign-in.
  final String? tokenLabel;
}

const _apps = {
  'github': _OneApp(
    'GitHub',
    '#181717',
    'Sign in with GitHub to see the pull requests waiting for your review, your issues and notifications.',
    'Allow Daksh to comment',
    'Full access lets Daksha comment on issues and pull requests and mark notifications read.',
    true,
  ),
  'notion': _OneApp(
    'Notion',
    '#000000',
    'Sign in with Notion and choose which pages and databases to share with Daksha.',
    'Allow Daksh to create and edit pages',
    'Full access lets Daksha create pages and add text.',
    false,
  ),
  'discord': _OneApp(
    'Discord',
    '#5865F2',
    'Create your own Discord bot in the Developer Portal and paste its token here. Daksha reads and posts only where the bot was invited.',
    'Allow Daksh to post messages',
    'Full access lets Daksha post in channels as the bot.',
    false,
    tokenLabel: 'Bot token',
  ),
  'spotify': _OneApp(
    'Spotify',
    '#1DB954',
    'Sign in with Spotify to see what is playing, your recent plays and playlists.',
    'Allow Daksh to control playback',
    'Full access lets Daksha play, pause, skip and queue songs (Spotify Premium).',
    true,
  ),
};

/// GitHub, Notion, Discord and Spotify on the phone: connect, the dashboard summary the server prepares, and Manage.
/// Their detailed screens (pull-request review, Notion pages, the player) are on the web.
class OneAppScreen extends ConsumerStatefulWidget {
  const OneAppScreen({super.key, required this.appKey});
  final String appKey;
  @override
  ConsumerState<OneAppScreen> createState() => _OneAppScreenState();
}

class _OneAppScreenState extends ConsumerState<OneAppScreen>
    with WidgetsBindingObserver {
  final _token = TextEditingController();
  bool _busy = false, _waiting = false;
  String? _error;

  _OneApp get _info =>
      _apps[widget.appKey] ??
      _OneApp(widget.appKey, '#888888', '', 'Full access', '', false);

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _token.dispose();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) _refresh();
  }

  void _refresh() {
    ref.invalidate(appStatusProvider(widget.appKey));
    ref.invalidate(appDashboardProvider(widget.appKey));
  }

  Future<void> _signIn({required bool write}) async {
    final url = await ref.read(apiProvider).oauthUrl(widget.appKey, {
      'write': '$write',
    });
    if (!await ref.read(linkOpenerProvider)(url))
      throw Exception('Could not open the browser to sign in.');
  }

  Future<void> _connect() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      if (_info.tokenLabel != null) {
        await ref.read(apiProvider).connectWithDetails(widget.appKey, {
          'token': _token.text.trim(),
        });
        _refresh();
      } else {
        await _signIn(write: false);
        if (mounted) setState(() => _waiting = true);
      }
    } catch (e) {
      if (mounted) setState(() => _error = errorText(e));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _manage(AppStatus st) {
    final i = _info;
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (_) => ManageSheet(
        config: ManageConfig(
          key: widget.appKey,
          name: i.name,
          color: i.color,
          hasWrite: true,
          writeLabel: i.writeLabel,
          writeHint: i.writeHint,
          writeNeedsSignIn: i.writeNeedsSignIn,
          settings: const [],
          onSignInWithWrite: () => _signIn(write: true),
        ),
        status: st,
        onChanged: _refresh,
      ),
    ).then((_) => _refresh());
  }

  @override
  Widget build(BuildContext context) {
    final i = _info;
    final status = ref.watch(appStatusProvider(widget.appKey));
    final scheme = Theme.of(context).colorScheme;
    return Scaffold(
      appBar: AppBar(
        title: Text(i.name),
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
        builder: (st) {
          if (!st.connected) {
            return ListView(
              padding: const EdgeInsets.all(12),
              children: [
                Tile(
                  padding: const EdgeInsets.all(20),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          AppBadge(name: i.name, color: i.color),
                          const SizedBox(width: 12),
                          Text(
                            'Connect ${i.name}',
                            style: Theme.of(context).textTheme.titleMedium,
                          ),
                        ],
                      ),
                      const SizedBox(height: 8),
                      Text(
                        i.blurb,
                        style: TextStyle(color: scheme.onSurfaceVariant),
                      ),
                      if (!st.configured && i.tokenLabel == null)
                        Padding(
                          padding: const EdgeInsets.only(top: 12),
                          child: Text(
                            '${i.name} is not set up on the server yet. An administrator needs to add its keys.',
                          ),
                        ),
                      if (i.tokenLabel != null)
                        Padding(
                          padding: const EdgeInsets.only(top: 12),
                          child: TextField(
                            controller: _token,
                            obscureText: true,
                            decoration: InputDecoration(
                              labelText: i.tokenLabel,
                            ),
                          ),
                        ),
                      if (_error != null)
                        Padding(
                          padding: const EdgeInsets.only(top: 8),
                          child: Text(
                            _error!,
                            style: TextStyle(color: scheme.error),
                          ),
                        ),
                      if (_waiting)
                        const Padding(
                          padding: EdgeInsets.only(top: 12),
                          child: Text(
                            'Finish signing in in your browser, then come back here.',
                          ),
                        ),
                      const SizedBox(height: 14),
                      Row(
                        children: [
                          FilledButton(
                            onPressed: _busy ? null : _connect,
                            child: Text(
                              _busy
                                  ? 'Working…'
                                  : i.tokenLabel != null
                                  ? 'Connect bot'
                                  : 'Continue with ${i.name}',
                            ),
                          ),
                          if (_waiting)
                            TextButton(
                              onPressed: _refresh,
                              child: const Text("I've finished"),
                            ),
                        ],
                      ),
                    ],
                  ),
                ),
              ],
            );
          }
          final dash = ref.watch(appDashboardProvider(widget.appKey));
          return RefreshIndicator(
            onRefresh: () async => _refresh(),
            child: ListView(
              padding: const EdgeInsets.all(12),
              children: [
                Text(
                  '${st.accountName ?? ''}${st.write ? '' : ' · read-only'}',
                  style: TextStyle(color: scheme.onSurfaceVariant),
                ),
                const SizedBox(height: 8),
                Tile(
                  padding: const EdgeInsets.all(16),
                  child: dash.when(
                    loading: () => const LinearProgressIndicator(),
                    error: (e, _) => Text(
                      errorText(e),
                      style: TextStyle(color: scheme.error),
                    ),
                    data: (d) => SectionsView(
                      dashboard: d,
                      onOpenLink: ref.read(linkOpenerProvider),
                    ),
                  ),
                ),
                const Padding(
                  padding: EdgeInsets.all(12),
                  child: Text(
                    'More of this app is on the web.',
                    textAlign: TextAlign.center,
                  ),
                ),
              ],
            ),
          );
        },
      ),
    );
  }
}
