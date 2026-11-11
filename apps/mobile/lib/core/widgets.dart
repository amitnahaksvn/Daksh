import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Renders loading / error (with retry) / data states for an AsyncValue.
class AsyncBody<T> extends StatelessWidget {
  const AsyncBody({super.key, required this.value, required this.onRetry, required this.builder});
  final AsyncValue<T> value;
  final VoidCallback onRetry;
  final Widget Function(T data) builder;

  @override
  Widget build(BuildContext context) => value.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => Center(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Container(
              padding: const EdgeInsets.all(20),
              decoration: BoxDecoration(color: Theme.of(context).colorScheme.errorContainer, borderRadius: BorderRadius.circular(22)),
              child: Column(mainAxisSize: MainAxisSize.min, children: [
                Semantics(
                  liveRegion: true,
                  child: Text(e.toString(), textAlign: TextAlign.center, style: TextStyle(color: Theme.of(context).colorScheme.onErrorContainer)),
                ),
                const SizedBox(height: 14),
                FilledButton(onPressed: onRetry, child: const Text('Try again')),
              ]),
            ),
          ),
        ),
        data: builder,
      );
}

/// A small status label: lime for a good state, pink for a simulated or pending one.
class StatusChip extends StatelessWidget {
  const StatusChip(this.label, {super.key, this.lime = false});
  final String label;
  final bool lime;

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
        decoration: BoxDecoration(color: lime ? const Color(0xFFC8F169) : const Color(0xFFFFC9D6), borderRadius: BorderRadius.circular(999)),
        child: Text(label, style: const TextStyle(color: Color(0xFF17181C), fontSize: 12, fontWeight: FontWeight.w700)),
      );
}

/// A white rounded tile: the Bento building block for lists and groups.
class Tile extends StatelessWidget {
  const Tile({super.key, required this.child, this.padding = EdgeInsets.zero});
  final Widget child;
  final EdgeInsetsGeometry padding;

  @override
  Widget build(BuildContext context) => Card(child: Padding(padding: padding, child: child));
}
