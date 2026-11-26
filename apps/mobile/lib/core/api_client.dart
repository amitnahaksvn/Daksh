import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:logging/logging.dart';

import 'app_models.dart';

/// Base URL of the Daksh API. Override with --dart-define=API_URL=...
/// (Android emulators reach the host machine at 10.0.2.2). Defaults to the hosted API.
const apiUrl = String.fromEnvironment('API_URL', defaultValue: 'https://daksh-api.salmonmushroom-0ed2f93e.centralindia.azurecontainerapps.io');

/// Local development only; the API ignores this unless Auth__Mode=Development.
const devUser = String.fromEnvironment('DEV_USER', defaultValue: '');

class ApiException implements Exception {
  ApiException(this.statusCode, this.message, [this.fieldErrors = const {}]);
  final int statusCode;
  final String message;
  final Map<String, List<String>> fieldErrors;

  @override
  String toString() => message;
}

class AssistantReply {
  AssistantReply(this.conversationId, this.reply, this.provider, this.isSimulated);
  factory AssistantReply.fromJson(Map<String, dynamic> j) => AssistantReply(
      j['conversationId'] as String, j['reply'] as String, j['provider'] as String, j['isSimulated'] as bool);
  final String conversationId;
  final String reply;
  final String provider;
  final bool isSimulated;
}

class CalendarEvent {
  CalendarEvent(this.id, this.title, this.start, this.allDay, this.location, [this.accountId = '', this.account = '', this.end = '']);
  factory CalendarEvent.fromJson(Map<String, dynamic> j) => CalendarEvent(j['id'] as String, j['title'] as String, j['start'] as String,
      j['allDay'] as bool, j['location'] as String?, (j['accountId'] as String?) ?? '', (j['account'] as String?) ?? '', (j['end'] as String?) ?? '');
  /// ISO text like [start].
  final String end;

  /// Length in minutes (60 when it can't be worked out, and for all-day events), for the edit form.
  int get minutes {
    if (allDay || end.isEmpty) return 60;
    try {
      final m = DateTime.parse(end).difference(DateTime.parse(start)).inMinutes;
      return m >= 5 ? m : 60;
    } catch (_) {
      return 60;
    }
  }
  /// Which connected Google account the event is on (needed to delete it, and shown when there is more than one).
  final String accountId;
  final String account;
  final String id;
  final String title;
  /// ISO text in the owner's timezone: "2026-11-20T17:00:00+05:30", or just "2026-11-20" for an all-day event.
  final String start;
  final bool allDay;
  final String? location;

  /// "2026-11-20", read straight from the text so it follows the owner's calendar timezone, not the phone's.
  String get day => start.substring(0, 10);

  /// "5:30 PM" or "All day".
  String get time {
    if (allDay) return 'All day';
    final h = int.parse(start.substring(11, 13));
    return '${h % 12 == 0 ? 12 : h % 12}:${start.substring(14, 16)} ${h < 12 ? 'AM' : 'PM'}';
  }
}

class CalendarAccount {
  CalendarAccount(this.id, this.email, this.hasCalendar, this.error);
  factory CalendarAccount.fromJson(Map<String, dynamic> j) =>
      CalendarAccount(j['id'] as String, j['email'] as String, j['hasCalendar'] as bool, j['error'] as String?);
  final String id;
  final String email;
  final bool hasCalendar;
  final String? error;
}

/// What the calendar endpoint returns: the events, or the reason there are none ([connected] false = Gmail was connected without Calendar).
class CalendarData {
  CalendarData(this.connected, this.message, this.today, this.events, [this.accounts = const []]);
  factory CalendarData.fromJson(Map<String, dynamic> j) => CalendarData(
      j['connected'] as bool,
      j['message'] as String?,
      j['today'] as String,
      ((j['events'] as List?) ?? const []).map((e) => CalendarEvent.fromJson(e as Map<String, dynamic>)).toList(),
      ((j['accounts'] as List?) ?? const []).map((e) => CalendarAccount.fromJson(e as Map<String, dynamic>)).toList());
  final bool connected;
  final String? message;
  final String today;
  final List<CalendarEvent> events;
  final List<CalendarAccount> accounts;

  /// Accounts with Calendar access, i.e. the ones whose events are shown and that can be added to.
  List<CalendarAccount> get usable => accounts.where((a) => a.hasCalendar).toList();

  /// Accounts that are not shown: connected without Calendar, or unreadable right now.
  List<CalendarAccount> get notShown => accounts.where((a) => !a.hasCalendar || a.error != null).toList();
}

class TaskItem {
  TaskItem(this.id, this.title, this.completed);
  factory TaskItem.fromJson(Map<String, dynamic> j) =>
      TaskItem(j['id'] as String, j['title'] as String, j['completed'] as bool);
  final String id;
  final String title;
  final bool completed;
}

class IntegrationStatus {
  IntegrationStatus(this.key, this.state, this.note);
  factory IntegrationStatus.fromJson(Map<String, dynamic> j) =>
      IntegrationStatus(j['key'] as String, j['state'] as String, j['note'] as String);
  final String key;
  final String state;
  final String note;
}

class PrivacySettings {
  const PrivacySettings(this.memoryEnabled, this.allowProviderDataSharing, this.retentionDays);
  factory PrivacySettings.fromJson(Map<String, dynamic> j) => PrivacySettings(
      j['memoryEnabled'] as bool, j['allowProviderDataSharing'] as bool, j['retentionDays'] as int);
  final bool memoryEnabled;
  final bool allowProviderDataSharing;
  final int retentionDays;
  Map<String, dynamic> toJson() => {
        'memoryEnabled': memoryEnabled,
        'allowProviderDataSharing': allowProviderDataSharing,
        'retentionDays': retentionDays,
      };
}

/// One tile on the dashboard: a connected app or a single account of an app.
class DashboardApp {
  DashboardApp(this.id, this.connectionId, this.key, this.name, this.status, this.account, this.unread, this.lastPreview);
  factory DashboardApp.fromJson(Map<String, dynamic> j) => DashboardApp(j['id'] as String, j['connectionId'] as String, j['integrationKey'] as String,
      j['name'] as String, j['status'] as String, j['accountIdentifier'] as String?, (j['unreadCount'] as num?)?.toInt() ?? 0, j['lastMessagePreview'] as String?);
  final String id, connectionId, key, name, status;
  final String? account, lastPreview;
  final int unread;
}

/// A connected app or account that can be shown (or not) on the dashboard.
class AvailableApp {
  AvailableApp(this.connectionId, this.key, this.name, this.account, this.dashboardId);
  factory AvailableApp.fromJson(Map<String, dynamic> j) => AvailableApp(
      j['connectionId'] as String, j['integrationKey'] as String, j['name'] as String, j['accountIdentifier'] as String?, j['dashboardId'] as String?);
  final String connectionId, key, name;
  final String? account, dashboardId;
}

class GmailPriority {
  GmailPriority(this.from, this.subject, this.label, this.unread);
  final String from, subject, label;
  final bool unread;
}

/// The numbers and priority emails of one Gmail account.
class GmailSummary {
  GmailSummary(this.email, this.today, this.unread, this.important, this.priority, this.syncError);
  factory GmailSummary.fromJson(Map<String, dynamic> j) {
    final counts = j['counts'] as Map<String, dynamic>;
    final sync = (j['sync'] as Map<String, dynamic>?) ?? const {};
    return GmailSummary(
      ((j['account'] as Map<String, dynamic>)['email'] as String?) ?? '',
      (counts['today'] as num).toInt(), (counts['unread'] as num).toInt(), (counts['important'] as num).toInt(),
      [for (final p in (j['priority'] as List)) GmailPriority(p['from'] as String? ?? '', p['subject'] as String? ?? '', p['label'] as String? ?? '', p['unread'] == true)],
      sync['status'] == 'Failed' ? sync['lastError'] as String? : null,
    );
  }
  final String email;
  final int today, unread, important;
  final List<GmailPriority> priority;
  final String? syncError;
}

/// A chat in Telegram or WhatsApp.
class ChatSummary {
  ChatSummary(this.chatId, this.name, this.isGroup, this.preview, this.unread);
  final String chatId, name, preview;
  final bool isGroup;
  final int unread;
}

class ChatMessage {
  ChatMessage(this.fromMe, this.text, this.at, this.sender);
  final bool fromMe;
  final String text;
  final DateTime? at;
  final String? sender;
}

class ConversationSummary {
  ConversationSummary(this.id, this.title);
  final String id;
  final String title;
}

/// Thin client for the shared Daksh API. All business logic lives server-side.
class DakshApi {
  DakshApi({http.Client? client, String? baseUrl, this.accessToken})
      : _client = client ?? http.Client(),
        _baseUrl = baseUrl ?? apiUrl;

  final http.Client _client;
  final String _baseUrl;
  final _log = Logger('DakshApi');

  /// Set by the authentication integration once the user has signed in.
  String? accessToken;

  Map<String, String> get _headers => {
        'Content-Type': 'application/json',
        if (accessToken != null) 'Authorization': 'Bearer $accessToken',
        if (accessToken == null && devUser.isNotEmpty) 'X-Dev-User': devUser,
      };

  Future<dynamic> _send(String method, String path, [Object? body]) async {
    final request = http.Request(method, Uri.parse('$_baseUrl$path'))
      ..headers.addAll(_headers);
    if (body != null) request.body = jsonEncode(body);
    http.Response res;
    try {
      res = await http.Response.fromStream(await _client.send(request));
    } catch (e, st) {
      _log.warning('Network error on $method $path', e, st);
      throw ApiException(0, 'Cannot reach the Daksh API. Check your connection and try again.');
    }
    if (res.statusCode >= 200 && res.statusCode < 300) {
      return res.body.isEmpty ? null : jsonDecode(res.body);
    }
    var message = 'Request failed (${res.statusCode})';
    var fields = <String, List<String>>{};
    try {
      final j = jsonDecode(res.body) as Map<String, dynamic>;
      message = (j['title'] as String?) ?? message;
      fields = ((j['errors'] as Map<String, dynamic>?) ?? {})
          .map((k, v) => MapEntry(k, (v as List).cast<String>()));
    } catch (_) {/* non-JSON error body */}
    if (res.statusCode == 401) message = 'You need to sign in to continue.';
    _log.info('API error ${res.statusCode} on $method $path');
    throw ApiException(res.statusCode, message, fields);
  }

  Future<AssistantReply> sendMessage(String message, {String? conversationId}) async =>
      AssistantReply.fromJson(await _send('POST', '/api/v1/assistant/messages',
          {'message': message, 'conversationId': conversationId}) as Map<String, dynamic>);

  Future<List<TaskItem>> tasks() async =>
      ((await _send('GET', '/api/v1/tasks')) as List).map((e) => TaskItem.fromJson(e as Map<String, dynamic>)).toList();

  Future<CalendarData> calendar(String from, String to) async =>
      CalendarData.fromJson(await _send('GET', '/api/v1/calendar/events?from=$from&to=$to') as Map<String, dynamic>);

  Future<void> addCalendarEvent(String title, String startLocalIso, int minutes, String? location, {String? accountId}) =>
      _send('POST', '/api/v1/calendar/events',
          {'title': title, 'start': startLocalIso, 'minutes': minutes, 'location': location, 'accountId': accountId});

  /// Changes an event on the account it is on. Leave [startLocalIso] null to keep its times (all-day events); [location] "" clears the place.
  Future<void> updateCalendarEvent(String id, String accountId,
          {String? title, String? startLocalIso, int? minutes, String? location}) =>
      _send('PATCH', '/api/v1/calendar/events/${Uri.encodeComponent(id)}',
          {'title': title, 'start': startLocalIso, 'minutes': minutes, 'location': location, 'accountId': accountId});

  Future<void> deleteCalendarEvent(String id, {String? accountId}) => _send('DELETE',
      '/api/v1/calendar/events/${Uri.encodeComponent(id)}${accountId == null || accountId.isEmpty ? '' : '?account=${Uri.encodeComponent(accountId)}'}');

  Future<void> createTask(String title) => _send('POST', '/api/v1/tasks', {'title': title});

  Future<void> createReminder(String text, DateTime at) =>
      _send('POST', '/api/v1/reminders', {'text': text, 'remindAt': at.toUtc().toIso8601String()});

  Future<List<IntegrationStatus>> integrations() async => ((await _send('GET', '/api/v1/integrations')) as List)
      .map((e) => IntegrationStatus.fromJson(e as Map<String, dynamic>))
      .toList();

  Future<PrivacySettings> privacy() async =>
      PrivacySettings.fromJson(await _send('GET', '/api/v1/privacy') as Map<String, dynamic>);

  Future<void> updatePrivacy(PrivacySettings s) => _send('PUT', '/api/v1/privacy', s.toJson());

  // ── Account ────────────────────────────────────────────────────────────────────────────────────────────────────────
  /// Signs in with the account email and password; returns the access token.
  Future<String> login(String email, String password) async {
    final j = await _send('POST', '/api/v1/account/login', {'email': email, 'password': password}) as Map<String, dynamic>;
    return j['accessToken'] as String;
  }

  // ── Dashboard ──────────────────────────────────────────────────────────────────────────────────────────────────────
  Future<List<DashboardApp>> dashboardApps() async =>
      ((await _send('GET', '/api/dashboard/apps')) as List).map((e) => DashboardApp.fromJson(e as Map<String, dynamic>)).toList();

  Future<List<AvailableApp>> availableApps() async =>
      ((await _send('GET', '/api/dashboard/apps/available')) as List).map((e) => AvailableApp.fromJson(e as Map<String, dynamic>)).toList();

  Future<void> addToDashboard(String connectionId) => _send('POST', '/api/dashboard/apps', {'connectionId': connectionId});
  Future<void> removeFromDashboard(String id) => _send('DELETE', '/api/dashboard/apps/$id');

  Future<GmailSummary> gmailSummary(String accountId) async {
    final midnight = DateTime.now();
    final since = DateTime(midnight.year, midnight.month, midnight.day).millisecondsSinceEpoch ~/ 1000;
    return GmailSummary.fromJson(await _send('GET', '/api/integrations/gmail/dashboard?accountId=$accountId&since=$since') as Map<String, dynamic>);
  }

  // ── Chats (Telegram and WhatsApp) ────────────────────────────────────────────────────────────────────────────────────
  /// The user's connection id for an app (`telegram` or `whatsapp`), or null when it is not connected.
  Future<String?> connectionFor(String appKey) async {
    final j = await _send('GET', '/api/integrations/$appKey') as Map<String, dynamic>;
    final c = j['connection'] as Map<String, dynamic>?;
    return c != null && c['status'] == 'Connected' ? c['connectionId'] as String : null;
  }

  Future<List<ChatSummary>> chats(String appKey, String connectionId) async {
    final list = (await _send('GET', '/api/integrations/$appKey/$connectionId/chats')) as List;
    return [
      for (final c in list.cast<Map<String, dynamic>>())
        ChatSummary(c['chatId'] as String, ((c['name'] ?? c['chatName']) as String?) ?? (c['chatId'] as String), c['isGroup'] == true,
            (c['lastMessagePreview'] as String?) ?? '', (c['unreadCount'] as num?)?.toInt() ?? 0),
    ];
  }

  Future<List<ChatMessage>> chatMessages(String appKey, String connectionId, String chatId) async {
    final list = (await _send('GET', '/api/integrations/$appKey/$connectionId/chats/${Uri.encodeComponent(chatId)}/messages')) as List;
    return [
      for (final m in list.cast<Map<String, dynamic>>())
        ChatMessage(m['fromMe'] == true, (m['text'] as String?) ?? '', DateTime.tryParse((m['timestamp'] as String?) ?? '')?.toLocal(), m['senderName'] as String?),
    ];
  }

  Future<void> sendChatMessage(String appKey, String connectionId, String chatId, String text) =>
      _send('POST', '/api/integrations/$appKey/$connectionId/chats/${Uri.encodeComponent(chatId)}/messages', {'text': text});

  // ── Assistant history ───────────────────────────────────────────────────────────────────────────────────────────────
  Future<List<ConversationSummary>> conversations() async => [
        for (final c in ((await _send('GET', '/api/v1/assistant/conversations')) as List).cast<Map<String, dynamic>>())
          ConversationSummary(c['id'] as String, ((c['title'] as String?)?.trim().isNotEmpty ?? false) ? (c['title'] as String).trim() : 'New chat'),
      ];

  /// The messages of one past conversation as (isUser, text).
  Future<List<(bool, String)>> conversationMessages(String id) async => [
        for (final m in ((await _send('GET', '/api/v1/assistant/conversations/$id/messages')) as List).cast<Map<String, dynamic>>())
          (m['role'] == 'user', (m['content'] as String?) ?? ''),
      ];

  Future<void> deleteConversation(String id) => _send('DELETE', '/api/v1/assistant/conversations/$id');

  // ── Apps (catalogue, and every app that signs in once) ───────────────────────────────────────────────────────────────
  Future<List<AppCatalogEntry>> appCatalog() async => [
    for (final e in (await _send('GET', '/api/integrations')) as List)
      AppCatalogEntry.fromJson(e as Map<String, dynamic>),
  ];

  Future<AppStatus> appStatus(String key) async => AppStatus.fromJson(
    await _send('GET', '/api/integrations/$key/status') as Map<String, dynamic>,
  );

  /// Which provider-engine apps are connected, in one call.
  Future<Map<String, bool>> providerStatuses() async =>
      (await _send('GET', '/api/integrations/provider-apps/statuses')
              as Map<String, dynamic>)
          .map((k, v) => MapEntry(k, v == true));

  /// The address to open in the browser to sign in to an OAuth app. [params] carry choices such as `write` or a Shopify shop.
  Future<String> oauthUrl(String key, Map<String, String> params) async {
    final query = params.isEmpty
        ? ''
        : '?${Uri(queryParameters: params).query}';
    return (await _send('POST', '/api/integrations/$key/connect$query')
            as Map<String, dynamic>)['authorizationUrl']
        as String;
  }

  /// Credential and server-key apps connect by posting what they ask for.
  Future<void> connectWithDetails(String key, Map<String, String> values) =>
      _send('POST', '/api/integrations/$key/connect', values);

  Future<void> disconnectApp(String key) =>
      _send('POST', '/api/integrations/$key/disconnect');
  Future<void> setAppWrite(String key, bool enabled) =>
      _send('POST', '/api/integrations/$key/write', {'enabled': enabled});
  Future<Map<String, dynamic>> appSettings(String key) async =>
      (await _send('GET', '/api/integrations/$key/settings'))
          as Map<String, dynamic>;
  Future<Map<String, dynamic>> updateAppSettings(
    String key,
    Map<String, Object?> values,
  ) async =>
      (await _send('PUT', '/api/integrations/$key/settings', values))
          as Map<String, dynamic>;
  Future<AppDashboard> appDashboard(String key) async => AppDashboard.fromJson(
    await _send('GET', '/api/integrations/$key/dashboard')
        as Map<String, dynamic>,
  );

  Future<ProviderSpec> providerSpec(String key) async => ProviderSpec.fromJson(
    await _send('GET', '/api/integrations/$key/spec') as Map<String, dynamic>,
  );

  Future<List<ProviderItem>> providerView(
    String key,
    String view, [
    Map<String, String> params = const {},
  ]) async {
    final query = params.isEmpty
        ? ''
        : '?${Uri(queryParameters: params).query}';
    return [
      for (final e
          in (await _send('GET', '/api/integrations/$key/views/$view$query'))
              as List)
        ProviderItem.fromJson(e as Map<String, dynamic>),
    ];
  }

  Future<ProviderActionResult> providerAction(
    String key,
    String action,
    Map<String, String> inputs,
  ) async => ProviderActionResult.fromJson(
    await _send('POST', '/api/integrations/$key/actions/$action', {
          'inputs': inputs,
        })
        as Map<String, dynamic>,
  );

  // ── Lists, reminders, tasks, activity ───────────────────────────────────────────────────────────────────────────────
  Future<List<ListEntry>> lists() async => [
    for (final e in (await _send('GET', '/api/v1/lists')) as List)
      ListEntry.fromJson(e as Map<String, dynamic>),
  ];
  Future<void> addListItem(String list, String text) =>
      _send('POST', '/api/v1/lists', {'list': list, 'text': text});
  Future<void> toggleListItem(String id) =>
      _send('POST', '/api/v1/lists/$id/toggle');
  Future<void> deleteListItem(String id) =>
      _send('DELETE', '/api/v1/lists/$id');

  Future<List<ReminderItem>> reminders() async => [
    for (final e in (await _send('GET', '/api/v1/reminders')) as List)
      ReminderItem.fromJson(e as Map<String, dynamic>),
  ];
  Future<void> completeTask(String id) =>
      _send('POST', '/api/v1/tasks/$id/complete');

  Future<List<AuditEvent>> auditEvents() async => [
    for (final e in (await _send('GET', '/api/v1/audit-events')) as List)
      AuditEvent.fromJson(e as Map<String, dynamic>),
  ];
  Future<List<ActivityEntry>> activity() async => [
    for (final e in (await _send('GET', '/api/dashboard/activity')) as List)
      ActivityEntry.fromJson(e as Map<String, dynamic>),
  ];

  // ── Profile ──────────────────────────────────────────────────────────────────────────────────────────────────────────
  Future<UserDetails> me() async => UserDetails.fromJson(
    await _send('GET', '/api/v1/account/me') as Map<String, dynamic>,
  );
  Future<UserDetails> updateMe(
    String firstName,
    String lastName,
    String gender,
    String dateOfBirth,
  ) async => UserDetails.fromJson(
    await _send('PUT', '/api/v1/account/me', {
          'firstName': firstName,
          'lastName': lastName,
          'gender': gender,
          'dateOfBirth': dateOfBirth,
        })
        as Map<String, dynamic>,
  );
  Future<void> changePassword(String current, String next) => _send(
    'POST',
    '/api/v1/account/me/password',
    {'currentPassword': current, 'newPassword': next},
  );
  Future<Map<String, dynamic>> busy() async =>
      (await _send('GET', '/api/v1/account/me/busy')) as Map<String, dynamic>;
  Future<Map<String, dynamic>> updateBusy(Map<String, dynamic> profile) async =>
      (await _send('PUT', '/api/v1/account/me/busy', profile))
          as Map<String, dynamic>;

  // ── Email (Gmail) ────────────────────────────────────────────────────────────────────────────────────────────────────
  Future<MailPage> mail(String query, {String? pageToken}) async {
    final params = {'q': query, if (pageToken != null) 'pageToken': pageToken};
    return MailPage.fromJson(
      await _send(
            'GET',
            '/api/integrations/gmail/messages?${Uri(queryParameters: params).query}',
          )
          as Map<String, dynamic>,
    );
  }

  Future<MailMessage> mailMessage(String id) async => MailMessage.fromJson(
    await _send(
          'GET',
          '/api/integrations/gmail/messages/${Uri.encodeComponent(id)}',
        )
        as Map<String, dynamic>,
  );
  Future<void> sendMail(String to, String subject, String body) => _send(
    'POST',
    '/api/integrations/gmail/messages',
    {'to': to, 'subject': subject, 'body': body},
  );
}
