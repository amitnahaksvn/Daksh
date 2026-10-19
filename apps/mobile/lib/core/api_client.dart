import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:logging/logging.dart';

/// Base URL of the Daksh API. Override with --dart-define=API_URL=...
/// (Android emulators reach the host machine at 10.0.2.2).
const apiUrl = String.fromEnvironment('API_URL', defaultValue: 'http://localhost:5080');

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

  Future<void> createTask(String title) => _send('POST', '/api/v1/tasks', {'title': title});

  Future<void> createReminder(String text, DateTime at) =>
      _send('POST', '/api/v1/reminders', {'text': text, 'remindAt': at.toUtc().toIso8601String()});

  Future<List<IntegrationStatus>> integrations() async => ((await _send('GET', '/api/v1/integrations')) as List)
      .map((e) => IntegrationStatus.fromJson(e as Map<String, dynamic>))
      .toList();

  Future<PrivacySettings> privacy() async =>
      PrivacySettings.fromJson(await _send('GET', '/api/v1/privacy') as Map<String, dynamic>);

  Future<void> updatePrivacy(PrivacySettings s) => _send('PUT', '/api/v1/privacy', s.toJson());
}
