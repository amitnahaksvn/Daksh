import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/api_client.dart';
import '../core/providers.dart';
import '../core/widgets.dart';

/// Which chat app a list or window belongs to.
const _apps = [('telegram', 'Telegram'), ('whatsapp', 'WhatsApp')];

/// One chat app's connection id (null when it is not connected) and its chats.
class _ChatList {
  _ChatList(this.connectionId, this.chats);
  final String? connectionId;
  final List<ChatSummary> chats;
}

final _chatListProvider = FutureProvider.autoDispose.family<_ChatList, String>((ref, app) async {
  final api = ref.watch(apiProvider);
  final id = await api.connectionFor(app);
  return _ChatList(id, id == null ? const [] : await api.chats(app, id));
});

/// The Chats tab: Telegram and WhatsApp, one list each.
class ChatsScreen extends StatelessWidget {
  const ChatsScreen({super.key});

  @override
  Widget build(BuildContext context) => DefaultTabController(
        length: _apps.length,
        child: Column(children: [
          TabBar(tabs: [for (final a in _apps) Tab(text: a.$2)]),
          Expanded(child: TabBarView(children: [for (final a in _apps) _ChatListView(app: a.$1, label: a.$2)])),
        ]),
      );
}

class _ChatListView extends ConsumerWidget {
  const _ChatListView({required this.app, required this.label});
  final String app, label;

  @override
  Widget build(BuildContext context, WidgetRef ref) => AsyncBody(
        value: ref.watch(_chatListProvider(app)),
        onRetry: () => ref.invalidate(_chatListProvider(app)),
        builder: (list) {
          if (list.connectionId == null) {
            return Center(child: Padding(padding: const EdgeInsets.all(24), child: Text('$label is not connected. Connect it from the web app.', textAlign: TextAlign.center)));
          }
          if (list.chats.isEmpty) return const Center(child: Text('No chats yet.'));
          return RefreshIndicator(
            onRefresh: () async => ref.invalidate(_chatListProvider(app)),
            child: ListView.separated(
              itemCount: list.chats.length,
              separatorBuilder: (_, _) => const Divider(height: 1, indent: 72),
              itemBuilder: (context, i) {
                final c = list.chats[i];
                return ListTile(
                  leading: CircleAvatar(child: Icon(c.isGroup ? Icons.group : Icons.person)),
                  title: Text(c.name, maxLines: 1, overflow: TextOverflow.ellipsis),
                  subtitle: Text(c.preview, maxLines: 1, overflow: TextOverflow.ellipsis),
                  trailing: c.unread > 0 ? Badge(label: Text('${c.unread}')) : null,
                  onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => ChatWindow(app: app, connectionId: list.connectionId!, chat: c))),
                );
              },
            ),
          );
        },
      );
}

/// One chat: the messages and a box to write in.
class ChatWindow extends ConsumerStatefulWidget {
  const ChatWindow({super.key, required this.app, required this.connectionId, required this.chat});
  final String app, connectionId;
  final ChatSummary chat;
  @override
  ConsumerState<ChatWindow> createState() => _ChatWindowState();
}

class _ChatWindowState extends ConsumerState<ChatWindow> {
  final _text = TextEditingController();
  List<ChatMessage>? _messages;
  String? _error;
  bool _sending = false;
  Timer? _poll;

  @override
  void initState() {
    super.initState();
    _load();
    _poll = Timer.periodic(const Duration(seconds: 8), (_) => _load());
  }

  @override
  void dispose() {
    _poll?.cancel();
    _text.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final m = await ref.read(apiProvider).chatMessages(widget.app, widget.connectionId, widget.chat.chatId);
      if (mounted) setState(() { _messages = m; _error = null; });
    } catch (e) {
      if (mounted && _messages == null) setState(() => _error = e.toString());
    }
  }

  Future<void> _send() async {
    final t = _text.text.trim();
    if (t.isEmpty || _sending) return;
    setState(() => _sending = true);
    try {
      await ref.read(apiProvider).sendChatMessage(widget.app, widget.connectionId, widget.chat.chatId, t);
      _text.clear();
      await _load();
    } catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.toString())));
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final messages = _messages;
    return Scaffold(
      appBar: AppBar(title: Text(widget.chat.name, overflow: TextOverflow.ellipsis)),
      body: Column(children: [
        Expanded(
          child: messages == null
              ? Center(child: _error != null ? Padding(padding: const EdgeInsets.all(24), child: Text(_error!)) : const CircularProgressIndicator())
              : messages.isEmpty
                  ? const Center(child: Text('No messages yet.'))
                  : ListView.builder(
                      reverse: true,
                      padding: const EdgeInsets.all(12),
                      itemCount: messages.length,
                      itemBuilder: (_, i) {
                        final m = messages[messages.length - 1 - i];
                        return Align(
                          alignment: m.fromMe ? Alignment.centerRight : Alignment.centerLeft,
                          child: Container(
                            margin: const EdgeInsets.symmetric(vertical: 3),
                            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                            constraints: BoxConstraints(maxWidth: MediaQuery.of(context).size.width * 0.78),
                            decoration: BoxDecoration(color: m.fromMe ? scheme.primaryContainer : scheme.surfaceContainerHighest, borderRadius: BorderRadius.circular(16)),
                            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                              if (!m.fromMe && widget.chat.isGroup && m.sender != null) Text(m.sender!, style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: scheme.primary)),
                              Text(m.text.isEmpty ? '(media)' : m.text),
                              if (m.at != null) Text(TimeOfDay.fromDateTime(m.at!).format(context), style: TextStyle(fontSize: 10, color: scheme.onSurfaceVariant)),
                            ]),
                          ),
                        );
                      },
                    ),
        ),
        SafeArea(
          top: false,
          child: Padding(
            padding: const EdgeInsets.fromLTRB(10, 6, 10, 8),
            child: Row(children: [
              Expanded(
                child: TextField(
                  controller: _text,
                  minLines: 1,
                  maxLines: 4,
                  textInputAction: TextInputAction.send,
                  onSubmitted: (_) => _send(),
                  decoration: InputDecoration(hintText: 'Write a message…', isDense: true, border: OutlineInputBorder(borderRadius: BorderRadius.circular(24))),
                ),
              ),
              const SizedBox(width: 8),
              IconButton.filled(onPressed: _sending ? null : _send, icon: const Icon(Icons.send), tooltip: 'Send'),
            ]),
          ),
        ),
      ]),
    );
  }
}
