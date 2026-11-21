import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/providers.dart';
import 'app_drawer.dart';

const _suggestions = [
  ('Catch me up on today', Icons.today_outlined),
  ("What's unread on WhatsApp?", Icons.mark_chat_unread_outlined),
  ('Any new emails in my inbox?', Icons.mail_outline),
  ('What tasks are open?', Icons.checklist),
];

/// The home screen: a chat, laid out like ChatGPT. A menu opens the drawer with history and the other pages.
class AssistantScreen extends ConsumerStatefulWidget {
  const AssistantScreen({super.key});
  @override
  ConsumerState<AssistantScreen> createState() => _AssistantScreenState();
}

class _AssistantScreenState extends ConsumerState<AssistantScreen> {
  final _controller = TextEditingController();
  final _scroll = ScrollController();
  bool _hasText = false;

  @override
  void initState() {
    super.initState();
    _controller.addListener(() {
      final has = _controller.text.trim().isNotEmpty;
      if (has != _hasText) setState(() => _hasText = has);
    });
  }

  @override
  void dispose() {
    _controller.dispose();
    _scroll.dispose();
    super.dispose();
  }

  Future<void> _send([String? preset]) async {
    final text = preset ?? _controller.text;
    if (text.trim().isEmpty) return;
    _controller.clear();
    await ref.read(assistantProvider.notifier).send(text);
  }

  @override
  Widget build(BuildContext context) {
    final chat = ref.watch(assistantProvider);
    final scheme = Theme.of(context).colorScheme;
    ref.listen<AssistantState>(assistantProvider, (_, _) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (_scroll.hasClients) _scroll.animateTo(_scroll.position.maxScrollExtent, duration: const Duration(milliseconds: 250), curve: Curves.easeOut);
      });
    });
    return Scaffold(
      drawer: const AppDrawer(),
      appBar: AppBar(
        centerTitle: true,
        title: const Text('Daksha', style: TextStyle(fontSize: 18, fontWeight: FontWeight.w600, letterSpacing: 0)),
        actions: [
          IconButton(tooltip: 'New chat', icon: const Icon(Icons.edit_square), onPressed: () => ref.read(assistantProvider.notifier).newChat()),
        ],
      ),
      body: Column(children: [
        Expanded(
          child: chat.lines.isEmpty && !chat.busy
              ? _Welcome(onPick: _send)
              : ListView(controller: _scroll, padding: const EdgeInsets.fromLTRB(16, 8, 16, 16), children: [
                  for (final (isUser, text) in chat.lines) _Message(isUser: isUser, text: text),
                  if (chat.busy) const _Thinking(),
                ]),
        ),
        if (chat.error != null)
          Padding(padding: const EdgeInsets.fromLTRB(20, 0, 20, 8), child: Text(chat.error!, style: TextStyle(color: scheme.error, fontWeight: FontWeight.w600))),
        _Composer(controller: _controller, hasText: _hasText, busy: chat.busy, onSend: _send),
      ]),
    );
  }
}

class _Welcome extends StatelessWidget {
  const _Welcome({required this.onPick});
  final void Function(String) onPick;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.all(24),
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Text('What can I help with?', textAlign: TextAlign.center, style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w600)),
          const SizedBox(height: 28),
          Wrap(spacing: 8, runSpacing: 8, alignment: WrapAlignment.center, children: [
            for (final (label, icon) in _suggestions)
              ActionChip(
                avatar: Icon(icon, size: 18, color: scheme.onSurfaceVariant),
                label: Text(label),
                onPressed: () => onPick(label),
                backgroundColor: Colors.transparent,
                side: BorderSide(color: scheme.outlineVariant),
                shape: const StadiumBorder(),
              ),
          ]),
        ]),
      ),
    );
  }
}

/// ChatGPT's message style: your words in a rounded grey bubble on the right; the assistant's reply as plain text on the left.
class _Message extends StatelessWidget {
  const _Message({required this.isUser, required this.text});
  final bool isUser;
  final String text;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    if (isUser) {
      return Align(
        alignment: Alignment.centerRight,
        child: Container(
          margin: const EdgeInsets.only(top: 14, bottom: 6, left: 48),
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
          decoration: BoxDecoration(color: scheme.surfaceContainer, borderRadius: BorderRadius.circular(22)),
          child: SelectableText(text, style: const TextStyle(fontSize: 16, height: 1.4)),
        ),
      );
    }
    return Padding(
      padding: const EdgeInsets.only(top: 6, bottom: 10),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        SelectableText(text, style: const TextStyle(fontSize: 16, height: 1.5)),
        const SizedBox(height: 6),
        Row(children: [
          IconButton(
            visualDensity: VisualDensity.compact,
            tooltip: 'Copy',
            icon: Icon(Icons.content_copy, size: 18, color: scheme.onSurfaceVariant),
            onPressed: () => ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Select the text to copy it'), duration: Duration(seconds: 1))),
          ),
        ]),
      ]),
    );
  }
}

class _Thinking extends StatelessWidget {
  const _Thinking();
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 12),
        child: Row(children: [
          Container(width: 12, height: 12, decoration: BoxDecoration(color: Theme.of(context).colorScheme.onSurface, shape: BoxShape.circle)),
        ]),
      );
}

/// The rounded input bar: a + on the left, the text, and a round send button on the right.
class _Composer extends StatelessWidget {
  const _Composer({required this.controller, required this.hasText, required this.busy, required this.onSend});
  final TextEditingController controller;
  final bool hasText, busy;
  final Future<void> Function([String?]) onSend;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return SafeArea(
      top: false,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(12, 4, 12, 10),
        child: Container(
          padding: const EdgeInsets.fromLTRB(6, 6, 6, 6),
          decoration: BoxDecoration(color: scheme.surfaceContainer, borderRadius: BorderRadius.circular(28)),
          child: Row(crossAxisAlignment: CrossAxisAlignment.end, children: [
            IconButton(tooltip: 'Attach', icon: const Icon(Icons.add), onPressed: () {}),
            Expanded(
              child: TextField(
                controller: controller,
                minLines: 1,
                maxLines: 6,
                textInputAction: TextInputAction.send,
                onSubmitted: (_) => onSend(),
                style: const TextStyle(fontSize: 16),
                decoration: InputDecoration(
                  hintText: 'Message',
                  hintStyle: TextStyle(color: scheme.onSurfaceVariant),
                  border: InputBorder.none,
                  enabledBorder: InputBorder.none,
                  focusedBorder: InputBorder.none,
                  filled: false,
                  isDense: true,
                  contentPadding: const EdgeInsets.symmetric(vertical: 12),
                ),
              ),
            ),
            const SizedBox(width: 4),
            IconButton.filled(
              tooltip: 'Send',
              style: IconButton.styleFrom(backgroundColor: scheme.primary, foregroundColor: scheme.onPrimary, disabledBackgroundColor: scheme.onSurface.withValues(alpha: 0.15)),
              onPressed: hasText && !busy ? () => onSend() : null,
              icon: const Icon(Icons.arrow_upward),
            ),
          ]),
        ),
      ),
    );
  }
}
