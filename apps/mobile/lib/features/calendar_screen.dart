import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/api_client.dart';
import '../core/providers.dart';
import '../core/widgets.dart';

const _months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const _days = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const _lengths = [15, 30, 45, 60, 90, 120, 240];

/// "Fri 20 Nov", with "Today" for today. [day] is "yyyy-MM-dd".
String dayHeading(String day, String today) {
  final d = DateTime.parse(day);
  final label = '${_days[d.weekday - 1]} ${d.day} ${_months[d.month - 1]}';
  return day == today ? 'Today · $label' : label;
}

String _lengthLabel(int m) => m < 60 ? '$m min' : m == 60 ? '1 hour' : '${m / 60} hours';

/// Your Google Calendar (all connected Google accounts) for the next 30 days, grouped by day, with add, edit and delete.
class CalendarScreen extends ConsumerWidget {
  const CalendarScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final value = ref.watch(calendarProvider);
    final canAdd = value.asData?.value.connected ?? false;
    return Scaffold(
      floatingActionButton: canAdd
          ? FloatingActionButton.extended(
              onPressed: () => _editor(context, ref, value.asData!.value.usable),
              icon: const Icon(Icons.add),
              label: const Text('Add event'))
          : null,
      body: AsyncBody(
        value: value,
        onRetry: () => ref.invalidate(calendarProvider),
        builder: (data) {
          if (!data.connected) {
            return const Center(
              child: Padding(
                padding: EdgeInsets.all(24),
                child: Text(
                  "Google Calendar isn't connected yet. Connect your Google account in the Daksha web app (Calendar page) and tick the Calendar permission. One Google sign-in covers Gmail and Calendar.",
                  textAlign: TextAlign.center,
                ),
              ),
            );
          }
          final byDay = <String, List<CalendarEvent>>{};
          for (final e in data.events) {
            byDay.putIfAbsent(e.day, () => []).add(e);
          }
          final keys = byDay.keys.toList()..sort();
          return RefreshIndicator(
            onRefresh: () async => ref.refresh(calendarProvider.future),
            child: ListView(padding: const EdgeInsets.fromLTRB(12, 4, 12, 88), children: [
              if (data.notShown.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.fromLTRB(8, 8, 8, 0),
                  child: Text('Not shown: ${data.notShown.map((a) => a.email).join(', ')}. Connect it again in the web app and tick Calendar.',
                      style: TextStyle(color: Theme.of(context).colorScheme.error, fontSize: 13)),
                ),
              if (data.events.isEmpty)
                const Padding(padding: EdgeInsets.only(top: 80), child: Center(child: Text('Nothing on your calendar for the next 30 days.'))),
              for (final k in keys) ...[
                Padding(
                  padding: const EdgeInsets.fromLTRB(8, 12, 8, 6),
                  child: Text(dayHeading(k, data.today), style: const TextStyle(fontWeight: FontWeight.w700)),
                ),
                Tile(
                  child: Column(children: [
                    for (var i = 0; i < byDay[k]!.length; i++) ...[
                      if (i > 0) const Divider(indent: 16, endIndent: 16),
                      _EventRow(
                        event: byDay[k]![i],
                        showAccount: data.usable.length > 1,
                        onEdit: () => _editor(context, ref, data.usable, event: byDay[k]![i]),
                        onDelete: () => _delete(context, ref, byDay[k]![i]),
                      ),
                    ],
                  ]),
                ),
              ],
            ]),
          );
        },
      ),
    );
  }

  Future<void> _delete(BuildContext context, WidgetRef ref, CalendarEvent e) async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('Delete event?'),
        content: Text('"${e.title}" will be removed from your Google Calendar.'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('Delete')),
        ],
      ),
    );
    if (ok != true) return;
    try {
      await ref.read(apiProvider).deleteCalendarEvent(e.id, accountId: e.accountId);
      ref.invalidate(calendarProvider);
    } catch (err) {
      if (context.mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(err.toString())));
    }
  }

  /// Add a new event, or edit [event] (an all-day event keeps its dates; only its title and place change).
  Future<void> _editor(BuildContext context, WidgetRef ref, List<CalendarAccount> accounts, {CalendarEvent? event}) async {
    final editing = event != null;
    final title = TextEditingController(text: event?.title ?? '');
    final place = TextEditingController(text: event?.location ?? '');
    var date = editing ? DateTime.parse(event.day) : DateTime.now();
    var time = editing && !event.allDay
        ? TimeOfDay(hour: int.parse(event.start.substring(11, 13)), minute: int.parse(event.start.substring(14, 16)))
        : const TimeOfDay(hour: 9, minute: 0);
    var minutes = event?.minutes ?? 60;
    var accountId = accounts.isEmpty ? null : accounts.first.id;
    final timed = !editing || !event.allDay;
    final saved = await showDialog<bool>(
      context: context,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => AlertDialog(
          title: Text(editing ? 'Edit event' : 'New event'),
          content: SingleChildScrollView(
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              TextField(controller: title, autofocus: true, decoration: const InputDecoration(labelText: 'Title')),
              const SizedBox(height: 8),
              if (timed) ...[
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: const Icon(Icons.event),
                  title: Text('${_days[date.weekday - 1]} ${date.day} ${_months[date.month - 1]} ${date.year}'),
                  onTap: () async {
                    final d = await showDatePicker(context: c, initialDate: date, firstDate: DateTime.now().subtract(const Duration(days: 365)), lastDate: DateTime.now().add(const Duration(days: 730)));
                    if (d != null) set(() => date = d);
                  },
                ),
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: const Icon(Icons.schedule),
                  title: Text(time.format(c)),
                  onTap: () async {
                    final t = await showTimePicker(context: c, initialTime: time);
                    if (t != null) set(() => time = t);
                  },
                ),
                DropdownButtonFormField<int>(
                  initialValue: minutes,
                  decoration: const InputDecoration(labelText: 'Length'),
                  items: [for (final m in {..._lengths, minutes}.toList()..sort()) DropdownMenuItem(value: m, child: Text(_lengthLabel(m)))],
                  onChanged: (v) => set(() => minutes = v ?? 60),
                ),
              ],
              if (!editing && accounts.length > 1)
                DropdownButtonFormField<String>(
                  initialValue: accountId,
                  isExpanded: true,
                  decoration: const InputDecoration(labelText: 'Add to account'),
                  items: [for (final a in accounts) DropdownMenuItem(value: a.id, child: Text(a.email, overflow: TextOverflow.ellipsis))],
                  onChanged: (v) => set(() => accountId = v),
                ),
              TextField(controller: place, decoration: const InputDecoration(labelText: 'Location (optional)')),
            ]),
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Cancel')),
            FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(editing ? 'Save' : 'Add')),
          ],
        ),
      ),
    );
    if (saved != true || title.text.trim().isEmpty) return;
    String two(int n) => n.toString().padLeft(2, '0');
    final start = '${date.year}-${two(date.month)}-${two(date.day)}T${two(time.hour)}:${two(time.minute)}:00';
    final api = ref.read(apiProvider);
    try {
      if (editing) {
        await api.updateCalendarEvent(event.id, event.accountId,
            title: title.text.trim(), startLocalIso: timed ? start : null, minutes: timed ? minutes : null, location: place.text.trim());
      } else {
        await api.addCalendarEvent(title.text.trim(), start, minutes, place.text.trim().isEmpty ? null : place.text.trim(), accountId: accountId);
      }
      ref.invalidate(calendarProvider);
    } catch (err) {
      if (context.mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(err.toString())));
    }
  }
}

class _EventRow extends StatelessWidget {
  const _EventRow({required this.event, required this.onDelete, required this.onEdit, required this.showAccount});
  final CalendarEvent event;
  final bool showAccount;
  final VoidCallback onEdit;
  final VoidCallback onDelete;

  @override
  Widget build(BuildContext context) => ListTile(
        onTap: onEdit,
        leading: SizedBox(width: 64, child: Text(event.time, style: const TextStyle(fontWeight: FontWeight.w600))),
        title: Text(event.title),
        subtitle: (event.location == null && !(showAccount && event.account.isNotEmpty))
            ? null
            : Text([if (event.location != null) event.location!, if (showAccount && event.account.isNotEmpty) event.account].join(' · ')),
        trailing: Row(mainAxisSize: MainAxisSize.min, children: [
          IconButton(icon: const Icon(Icons.edit_outlined, size: 18), tooltip: 'Edit', onPressed: onEdit),
          IconButton(icon: const Icon(Icons.close, size: 18), tooltip: 'Delete', onPressed: onDelete),
        ]),
      );
}
