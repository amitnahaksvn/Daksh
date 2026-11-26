import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'api_client.dart';
import 'app_models.dart';
import 'links.dart';

final apiProvider = Provider<DakshApi>((ref) => DakshApi());

final tasksProvider = FutureProvider.autoDispose<List<TaskItem>>((ref) => ref.watch(apiProvider).tasks());
/// The next 30 days of the owner's Google Calendar, starting today.
final calendarProvider = FutureProvider.autoDispose<CalendarData>((ref) {
  String d(DateTime t) => '${t.year}-${t.month.toString().padLeft(2, '0')}-${t.day.toString().padLeft(2, '0')}';
  final now = DateTime.now();
  return ref.watch(apiProvider).calendar(d(now), d(now.add(const Duration(days: 30))));
});
final integrationsProvider =
    FutureProvider.autoDispose<List<IntegrationStatus>>((ref) => ref.watch(apiProvider).integrations());
final privacyProvider = FutureProvider.autoDispose<PrivacySettings>((ref) => ref.watch(apiProvider).privacy());

final dashboardAppsProvider = FutureProvider.autoDispose<List<DashboardApp>>((ref) => ref.watch(apiProvider).dashboardApps());
final availableAppsProvider = FutureProvider.autoDispose<List<AvailableApp>>((ref) => ref.watch(apiProvider).availableApps());
final gmailSummaryProvider =
    FutureProvider.autoDispose.family<GmailSummary, String>((ref, accountId) => ref.watch(apiProvider).gmailSummary(accountId));

final conversationsProvider = FutureProvider.autoDispose<List<ConversationSummary>>((ref) => ref.watch(apiProvider).conversations());

/// The conversation on screen: what was said, which conversation it is, and whether a reply is on its way.
class AssistantState {
  const AssistantState({this.lines = const [], this.conversationId, this.busy = false, this.error});
  final List<(bool, String)> lines; // (isUser, text)
  final String? conversationId;
  final bool busy;
  final String? error;
  AssistantState copy({List<(bool, String)>? lines, String? conversationId, bool? busy, String? error, bool clearError = false}) => AssistantState(
      lines: lines ?? this.lines, conversationId: conversationId ?? this.conversationId, busy: busy ?? this.busy, error: clearError ? null : (error ?? this.error));
}

final assistantProvider = NotifierProvider<AssistantNotifier, AssistantState>(AssistantNotifier.new);

class AssistantNotifier extends Notifier<AssistantState> {
  @override
  AssistantState build() => const AssistantState();

  void newChat() => state = const AssistantState();

  Future<void> open(String id) async {
    state = AssistantState(conversationId: id, busy: true);
    try {
      final lines = await ref.read(apiProvider).conversationMessages(id);
      state = AssistantState(conversationId: id, lines: lines);
    } catch (e) {
      state = AssistantState(conversationId: id, error: e.toString());
    }
  }

  Future<void> send(String text) async {
    final t = text.trim();
    if (t.isEmpty || state.busy) return;
    state = state.copy(lines: [...state.lines, (true, t)], busy: true, clearError: true);
    try {
      final r = await ref.read(apiProvider).sendMessage(t, conversationId: state.conversationId);
      state = state.copy(lines: [...state.lines, (false, r.isSimulated ? '${r.reply}\n(simulated reply)' : r.reply)], conversationId: r.conversationId, busy: false);
      ref.invalidate(conversationsProvider);
    } catch (e) {
      state = state.copy(busy: false, error: e.toString());
    }
  }
}

// ── Apps ───────────────────────────────────────────────────────────────────────────────────────────────────────────────
final appCatalogProvider = FutureProvider.autoDispose<List<AppCatalogEntry>>(
  (ref) => ref.watch(apiProvider).appCatalog(),
);
final providerStatusesProvider = FutureProvider.autoDispose<Map<String, bool>>(
  (ref) => ref.watch(apiProvider).providerStatuses(),
);
final providerSpecProvider = FutureProvider.autoDispose
    .family<ProviderSpec, String>(
      (ref, key) => ref.watch(apiProvider).providerSpec(key),
    );
final appStatusProvider = FutureProvider.autoDispose.family<AppStatus, String>(
  (ref, key) => ref.watch(apiProvider).appStatus(key),
);
final appDashboardProvider = FutureProvider.autoDispose
    .family<AppDashboard, String>(
      (ref, key) => ref.watch(apiProvider).appDashboard(key),
    );

// ── Everyday pages ─────────────────────────────────────────────────────────────────────────────────────────────────────
final listsProvider = FutureProvider.autoDispose<List<ListEntry>>(
  (ref) => ref.watch(apiProvider).lists(),
);
final remindersProvider = FutureProvider.autoDispose<List<ReminderItem>>(
  (ref) => ref.watch(apiProvider).reminders(),
);
final auditProvider = FutureProvider.autoDispose<List<AuditEvent>>(
  (ref) => ref.watch(apiProvider).auditEvents(),
);
final activityProvider = FutureProvider.autoDispose<List<ActivityEntry>>(
  (ref) => ref.watch(apiProvider).activity(),
);
final meProvider = FutureProvider.autoDispose<UserDetails>(
  (ref) => ref.watch(apiProvider).me(),
);
final mailProvider = FutureProvider.autoDispose.family<MailPage, String>(
  (ref, query) => ref.watch(apiProvider).mail(query),
);

/// Opens web addresses (sign-in pages, links in a list). Replaced in tests.
final linkOpenerProvider = Provider<LinkOpener>((ref) => openLink);
