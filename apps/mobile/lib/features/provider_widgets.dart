import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/app_models.dart';
import '../core/links.dart';
import '../core/providers.dart';
import '../core/widgets.dart';

String errorText(Object e) => e.toString().replaceFirst('Exception: ', '');

Color colorFromHex(String hex) {
  final h = hex.replaceFirst('#', '');
  final v = int.tryParse(h.length == 6 ? 'FF$h' : h, radix: 16);
  return Color(v ?? 0xFF888888);
}

/// A coloured circle with the app's first letter (the web app's brand tile, without needing every brand icon).
class AppBadge extends StatelessWidget {
  const AppBadge({
    super.key,
    required this.name,
    required this.color,
    this.size = 40,
  });
  final String name, color;
  final double size;
  @override
  Widget build(BuildContext context) {
    final c = colorFromHex(color);
    return Container(
      width: size,
      height: size,
      alignment: Alignment.center,
      decoration: BoxDecoration(
        color: c,
        borderRadius: BorderRadius.circular(size / 3),
      ),
      child: Text(
        name.isEmpty ? '?' : name.characters.first.toUpperCase(),
        style: TextStyle(
          color: ThemeData.estimateBrightnessForColor(c) == Brightness.dark
              ? Colors.white
              : Colors.black,
          fontWeight: FontWeight.w800,
          fontSize: size * 0.45,
        ),
      ),
    );
  }
}

/// One field of a server-described form: text-like inputs, pickers, a dropdown, or a dropdown filled from a list of the same app.
class InputField extends ConsumerStatefulWidget {
  const InputField({
    super.key,
    required this.appKey,
    required this.input,
    required this.value,
    required this.onChanged,
  });
  final String appKey;
  final ProviderInput input;
  final String value;
  final ValueChanged<String> onChanged;
  @override
  ConsumerState<InputField> createState() => _InputFieldState();
}

class _InputFieldState extends ConsumerState<InputField> {
  late final TextEditingController _c = TextEditingController(
    text: widget.value,
  );
  Future<List<ProviderItem>>? _choices;

  @override
  void initState() {
    super.initState();
    if (widget.input.kind == 'select-view' &&
        widget.input.optionsView != null) {
      _choices = ref
          .read(apiProvider)
          .providerView(widget.appKey, widget.input.optionsView!);
    }
  }

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  String _two(int n) => n.toString().padLeft(2, '0');

  Future<void> _pick({required bool time}) async {
    final now = DateTime.now();
    final date = await showDatePicker(
      context: context,
      initialDate: now,
      firstDate: DateTime(now.year - 1),
      lastDate: DateTime(now.year + 5),
    );
    if (date == null || !mounted) return;
    var text = '${date.year}-${_two(date.month)}-${_two(date.day)}';
    if (time) {
      final t = await showTimePicker(
        context: context,
        initialTime: TimeOfDay.fromDateTime(now),
      );
      if (t == null) return;
      text += 'T${_two(t.hour)}:${_two(t.minute)}';
    }
    _c.text = text;
    widget.onChanged(text);
  }

  @override
  Widget build(BuildContext context) {
    final i = widget.input;
    final label = i.required ? i.label : '${i.label} (optional)';
    final deco = InputDecoration(
      labelText: label,
      hintText: i.placeholder,
      helperText: i.hint,
      helperMaxLines: 3,
    );
    switch (i.kind) {
      case 'select':
        return DropdownButtonFormField<String>(
          initialValue: (i.options ?? const []).contains(widget.value)
              ? widget.value
              : null,
          decoration: deco,
          items: [
            for (final o in i.options ?? const <String>[])
              DropdownMenuItem(value: o, child: Text(o)),
          ],
          onChanged: (v) => widget.onChanged(v ?? ''),
        );
      case 'select-view':
        return FutureBuilder<List<ProviderItem>>(
          future: _choices,
          builder: (context, snap) {
            if (snap.hasError)
              return Text(
                errorText(snap.error!),
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              );
            if (!snap.hasData)
              return InputDecorator(
                decoration: deco,
                child: const LinearProgressIndicator(),
              );
            final items = snap.data!;
            if (items.isEmpty)
              return InputDecorator(
                decoration: deco,
                child: const Text('Nothing to choose from yet.'),
              );
            return DropdownButtonFormField<String>(
              initialValue: items.any((x) => x.id == widget.value)
                  ? widget.value
                  : null,
              isExpanded: true,
              decoration: deco,
              items: [
                for (final x in items)
                  DropdownMenuItem(
                    value: x.id,
                    child: Text(x.title, overflow: TextOverflow.ellipsis),
                  ),
              ],
              onChanged: (v) => widget.onChanged(v ?? ''),
            );
          },
        );
      case 'date':
      case 'datetime':
        return TextField(
          controller: _c,
          readOnly: true,
          decoration: deco.copyWith(suffixIcon: const Icon(Icons.event)),
          onTap: () => _pick(time: i.kind == 'datetime'),
        );
      default:
        return TextField(
          controller: _c,
          obscureText: i.kind == 'secret',
          autocorrect: i.kind != 'secret' && i.kind != 'url',
          enableSuggestions: i.kind != 'secret',
          keyboardType: i.kind == 'number'
              ? TextInputType.number
              : i.kind == 'url'
              ? TextInputType.url
              : i.kind == 'textarea'
              ? TextInputType.multiline
              : TextInputType.text,
          minLines: i.kind == 'textarea' ? 3 : 1,
          maxLines: i.kind == 'textarea' ? 6 : 1,
          decoration: deco,
          onChanged: widget.onChanged,
        );
    }
  }
}

Map<String, String> defaultsFor(List<ProviderInput> inputs) => {
  for (final i in inputs) i.key: i.defaultValue ?? '',
};

bool missingRequired(List<ProviderInput> inputs, Map<String, String> values) =>
    inputs.any((i) => i.required && (values[i.key] ?? '').trim().isEmpty);

/// A list of rows from any provider app: picture, title (opens the link), subtitle, badge, an Open button for rows with more inside, and long text.
class ItemList extends ConsumerWidget {
  const ItemList({
    super.key,
    required this.items,
    required this.empty,
    this.onDrill,
  });
  final List<ProviderItem> items;
  final String empty;
  final void Function(ProviderItem item)? onDrill;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (items.isEmpty)
      return Padding(
        padding: const EdgeInsets.all(24),
        child: Center(child: Text(empty, textAlign: TextAlign.center)),
      );
    final opener = ref.read(linkOpenerProvider);
    return Tile(
      child: Column(
        children: [
          for (var n = 0; n < items.length; n++) ...[
            if (n > 0) const Divider(height: 1, indent: 16, endIndent: 16),
            _ItemRow(item: items[n], onDrill: onDrill, opener: opener),
          ],
        ],
      ),
    );
  }
}

class _ItemRow extends StatelessWidget {
  const _ItemRow({
    required this.item,
    required this.onDrill,
    required this.opener,
  });
  final ProviderItem item;
  final void Function(ProviderItem item)? onDrill;
  final LinkOpener opener;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final image = item.image != null && item.image!.startsWith('https://')
        ? item.image!
        : null;
    final hasLink = item.url != null && isWebLink(item.url!);
    final canDrill = item.drill != null && onDrill != null;
    return InkWell(
      onTap: canDrill
          ? () => onDrill!(item)
          : hasLink
          ? () => opener(item.url!)
          : null,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                if (image != null)
                  Padding(
                    padding: const EdgeInsets.only(right: 12),
                    child: ClipRRect(
                      borderRadius: BorderRadius.circular(8),
                      child: Image.network(
                        image,
                        width: 44,
                        height: 44,
                        fit: BoxFit.cover,
                        errorBuilder: (_, _, _) =>
                            const SizedBox(width: 44, height: 44),
                      ),
                    ),
                  ),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        item.title,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(fontWeight: FontWeight.w600),
                      ),
                      if (item.subtitle != null && item.subtitle!.isNotEmpty)
                        Text(
                          item.subtitle!,
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                          style: TextStyle(
                            color: scheme.onSurfaceVariant,
                            fontSize: 13,
                          ),
                        ),
                    ],
                  ),
                ),
                if (item.badge != null)
                  Padding(
                    padding: const EdgeInsets.only(left: 8),
                    child: StatusChip(item.badge!, lime: true),
                  ),
                if (canDrill)
                  const Padding(
                    padding: EdgeInsets.only(left: 4),
                    child: Icon(Icons.chevron_right),
                  )
                else if (hasLink)
                  Padding(
                    padding: const EdgeInsets.only(left: 4),
                    child: Icon(
                      Icons.open_in_new,
                      size: 18,
                      color: scheme.onSurfaceVariant,
                    ),
                  ),
              ],
            ),
            if (item.body != null && item.body!.isNotEmpty)
              Container(
                margin: const EdgeInsets.only(top: 8),
                padding: const EdgeInsets.all(12),
                width: double.infinity,
                decoration: BoxDecoration(
                  color: scheme.surfaceContainer,
                  borderRadius: BorderRadius.circular(12),
                ),
                child: SelectableText(item.body!),
              ),
          ],
        ),
      ),
    );
  }
}

/// The sections a connected app prepares for its dashboard tile.
class SectionsView extends StatelessWidget {
  const SectionsView({super.key, required this.dashboard, this.onOpenLink});
  final AppDashboard dashboard;
  final LinkOpener? onOpenLink;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (final e in dashboard.errors)
          Padding(
            padding: const EdgeInsets.only(bottom: 6),
            child: Text(e, style: TextStyle(color: scheme.error, fontSize: 13)),
          ),
        for (final s in dashboard.sections) ...[
          Padding(
            padding: const EdgeInsets.only(top: 6, bottom: 4),
            child: Text(
              s.title.toUpperCase(),
              style: TextStyle(
                color: scheme.onSurfaceVariant,
                fontSize: 12,
                fontWeight: FontWeight.w700,
                letterSpacing: .6,
              ),
            ),
          ),
          if (s.items.isEmpty)
            const Padding(
              padding: EdgeInsets.only(bottom: 6),
              child: Text('Nothing to show.'),
            )
          else
            for (final i in s.items)
              ListTile(
                dense: true,
                contentPadding: EdgeInsets.zero,
                title: Text(
                  i.title,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(fontWeight: FontWeight.w600),
                ),
                subtitle: i.subtitle == null
                    ? null
                    : Text(
                        i.subtitle!,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                trailing: i.badge == null
                    ? null
                    : StatusChip(i.badge!, lime: true),
                onTap: i.url != null && onOpenLink != null && isWebLink(i.url!)
                    ? () => onOpenLink!(i.url!)
                    : null,
              ),
        ],
      ],
    );
  }
}
