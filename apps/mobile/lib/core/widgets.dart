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
            padding: const EdgeInsets.all(24),
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              Semantics(liveRegion: true, child: Text(e.toString(), textAlign: TextAlign.center)),
              const SizedBox(height: 12),
              FilledButton(onPressed: onRetry, child: const Text('Try again')),
            ]),
          ),
        ),
        data: builder,
      );
}
