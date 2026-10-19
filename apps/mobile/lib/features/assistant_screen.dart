import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/providers.dart';

class AssistantScreen extends ConsumerStatefulWidget {
  const AssistantScreen({super.key});
  @override
  ConsumerState<AssistantScreen> createState() => _AssistantScreenState();
}

class _AssistantScreenState extends ConsumerState<AssistantScreen> {
  final _controller = TextEditingController();
  final _lines = <(bool, String)>[]; // (isUser, text)
  String? _conversationId;
  String? _error;
  var _busy = false;

  Future<void> _send() async {
    final text = _controller.text.trim();
    if (text.isEmpty || _busy) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final r = await ref.read(apiProvider).sendMessage(text, conversationId: _conversationId);
      setState(() {
        _conversationId = r.conversationId;
        _lines
          ..add((true, text))
          ..add((false, r.isSimulated ? '${r.reply}\n(simulated reply)' : r.reply));
        _controller.clear();
      });
    } catch (e) {
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Column(children: [
        Expanded(
          child: ListView(padding: const EdgeInsets.all(16), children: [
            for (final (isUser, text) in _lines)
              Align(
                alignment: isUser ? Alignment.centerRight : Alignment.centerLeft,
                child: Card(child: Padding(padding: const EdgeInsets.all(12), child: Text(text))),
              ),
          ]),
        ),
        if (_error != null) Padding(padding: const EdgeInsets.all(8), child: Text(_error!, style: const TextStyle(color: Colors.red))),
        Padding(
          padding: const EdgeInsets.all(8),
          child: Row(children: [
            Expanded(
              child: TextField(
                controller: _controller,
                decoration: const InputDecoration(labelText: 'Message', border: OutlineInputBorder()),
                onSubmitted: (_) => _send(),
              ),
            ),
            const SizedBox(width: 8),
            FilledButton(onPressed: _busy ? null : _send, child: const Text('Send')),
          ]),
        ),
      ]);
}
