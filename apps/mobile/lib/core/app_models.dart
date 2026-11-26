/// Shapes of the apps, lists, reminders and profile endpoints (the same ones the web app uses).

Map<String, dynamic> _map(Object? o) =>
    o is Map ? o.cast<String, dynamic>() : <String, dynamic>{};
List<Map<String, dynamic>> _list(Object? o) =>
    o is List ? o.map(_map).toList() : <Map<String, dynamic>>[];
String? _str(Object? o) => o is String ? o : null;

class AppCatalogEntry {
  AppCatalogEntry(
    this.key,
    this.name,
    this.description,
    this.category,
    this.color,
    this.isEnabled,
  );
  factory AppCatalogEntry.fromJson(Map<String, dynamic> j) => AppCatalogEntry(
    j['integrationKey'] as String,
    j['name'] as String,
    (j['description'] as String?) ?? '',
    (j['category'] as String?) ?? 'More',
    (j['color'] as String?) ?? '#888888',
    j['isEnabled'] == true,
  );
  final String key, name, description, category, color;
  final bool isEnabled;
}

/// One account of an app that signs in once (GitHub, Notion, Discord, Spotify and every provider-engine app).
class AppStatus {
  AppStatus({
    required this.configured,
    required this.connected,
    this.connectionId,
    this.accountName,
    this.write = false,
    this.scopes,
    this.error,
  });
  factory AppStatus.fromJson(Map<String, dynamic> j) => AppStatus(
    configured: j['configured'] == true,
    connected: j['connected'] == true,
    connectionId: _str(j['connectionId']),
    accountName: _str(j['accountName']),
    write: j['write'] == true,
    scopes: _str(j['scopes']),
    error: _str(j['error']),
  );
  final bool configured, connected, write;
  final String? connectionId, accountName, scopes, error;
}

class DashItem {
  DashItem(this.id, this.title, this.subtitle, this.url, this.badge);
  factory DashItem.fromJson(Map<String, dynamic> j) => DashItem(
    j['id'] as String,
    j['title'] as String,
    _str(j['subtitle']),
    _str(j['url']),
    _str(j['badge']),
  );
  final String id, title;
  final String? subtitle, url, badge;
}

class DashSection {
  DashSection(this.title, this.href, this.items);
  factory DashSection.fromJson(Map<String, dynamic> j) => DashSection(
    j['title'] as String,
    _str(j['href']),
    _list(j['items']).map(DashItem.fromJson).toList(),
  );
  final String title;
  final String? href;
  final List<DashItem> items;
}

class AppDashboard {
  AppDashboard(this.connected, this.account, this.sections, this.errors);
  factory AppDashboard.fromJson(Map<String, dynamic> j) => AppDashboard(
    j['connected'] == true,
    _str(j['account']),
    _list(j['sections']).map(DashSection.fromJson).toList(),
    ((j['errors'] as List?) ?? const []).map((e) => e.toString()).toList(),
  );
  final bool connected;
  final String? account;
  final List<DashSection> sections;
  final List<String> errors;
}

// ── Provider-engine apps: the server describes each app and the screens draw themselves from that ──────────────────────

class ProviderInput {
  ProviderInput(
    this.key,
    this.label,
    this.kind,
    this.placeholder,
    this.required,
    this.options,
    this.optionsView,
    this.defaultValue,
    this.hint,
  );
  factory ProviderInput.fromJson(Map<String, dynamic> j) => ProviderInput(
    j['key'] as String,
    j['label'] as String,
    (j['kind'] as String?) ?? 'text',
    _str(j['placeholder']),
    j['required'] != false,
    (j['options'] as List?)?.map((e) => e.toString()).toList(),
    _str(j['optionsView']),
    _str(j['default']),
    _str(j['hint']),
  );
  final String key, label, kind;
  final String? placeholder, optionsView, defaultValue, hint;
  final bool required;
  final List<String>? options;
}

class ProviderView {
  ProviderView(
    this.id,
    this.label,
    this.takesArg,
    this.hidden,
    this.dashboard,
    this.empty,
    this.inputs,
  );
  factory ProviderView.fromJson(Map<String, dynamic> j) => ProviderView(
    j['id'] as String,
    j['label'] as String,
    j['takesArg'] == true,
    j['hidden'] == true,
    j['dashboard'] == true,
    _str(j['empty']),
    _list(j['inputs']).map(ProviderInput.fromJson).toList(),
  );
  final String id, label;
  final bool takesArg, hidden, dashboard;
  final String? empty;
  final List<ProviderInput> inputs;
  bool get needsInput => inputs.any((i) => i.required);
}

class ProviderAction {
  ProviderAction(this.id, this.label, this.hint, this.inputs);
  factory ProviderAction.fromJson(Map<String, dynamic> j) => ProviderAction(
    j['id'] as String,
    j['label'] as String,
    _str(j['hint']),
    _list(j['inputs']).map(ProviderInput.fromJson).toList(),
  );
  final String id, label;
  final String? hint;
  final List<ProviderInput> inputs;
}

class ProviderSetting {
  ProviderSetting(
    this.key,
    this.label,
    this.type,
    this.hint,
    this.placeholder,
    this.options,
    this.defaultValue,
  );
  factory ProviderSetting.fromJson(Map<String, dynamic> j) => ProviderSetting(
    j['key'] as String,
    j['label'] as String,
    j['type'] as String,
    _str(j['hint']),
    _str(j['placeholder']),
    (j['options'] as List?)?.map((e) => e.toString()).toList(),
    _str(j['default']),
  );
  final String key, label, type;
  final String? hint, placeholder, defaultValue;
  final List<String>? options;
}

class ProviderSpec {
  ProviderSpec({
    required this.key,
    required this.name,
    required this.color,
    required this.account,
    required this.connectTitle,
    required this.connectBlurb,
    required this.connectLabel,
    required this.writeLabel,
    required this.writeHint,
    required this.auth,
    required this.writeNeedsSignIn,
    required this.hasWrite,
    required this.setupDoc,
    required this.connectFields,
    required this.views,
    required this.actions,
    required this.settings,
  });
  factory ProviderSpec.fromJson(Map<String, dynamic> j) => ProviderSpec(
    key: j['key'] as String,
    name: j['name'] as String,
    color: (j['color'] as String?) ?? '#888888',
    account: (j['account'] as String?) ?? '',
    connectTitle: j['connectTitle'] as String,
    connectBlurb: j['connectBlurb'] as String,
    connectLabel: j['connectLabel'] as String,
    writeLabel: j['writeLabel'] as String,
    writeHint: j['writeHint'] as String,
    auth: j['auth'] as String,
    writeNeedsSignIn: j['writeNeedsSignIn'] == true,
    hasWrite: j['hasWrite'] != false,
    setupDoc: (j['setupDoc'] as String?) ?? '',
    connectFields: _list(
      j['connectFields'],
    ).map(ProviderInput.fromJson).toList(),
    views: _list(j['views']).map(ProviderView.fromJson).toList(),
    actions: _list(j['actions']).map(ProviderAction.fromJson).toList(),
    settings: _list(j['settings']).map(ProviderSetting.fromJson).toList(),
  );
  final String key,
      name,
      color,
      account,
      connectTitle,
      connectBlurb,
      connectLabel,
      writeLabel,
      writeHint,
      auth,
      setupDoc;
  final bool writeNeedsSignIn, hasWrite;
  final List<ProviderInput> connectFields;
  final List<ProviderView> views;
  final List<ProviderAction> actions;
  final List<ProviderSetting> settings;

  /// The lists shown as tabs (drill-down lists are reached from a row).
  List<ProviderView> get tabs => views.where((v) => !v.hidden).toList();
  bool get isOAuth => auth == 'oauth';
}

class ProviderItem {
  ProviderItem(
    this.id,
    this.title,
    this.subtitle,
    this.url,
    this.badge,
    this.image,
    this.drill,
    this.body,
  );
  factory ProviderItem.fromJson(Map<String, dynamic> j) => ProviderItem(
    j['id'] as String,
    j['title'] as String,
    _str(j['subtitle']),
    _str(j['url']),
    _str(j['badge']),
    _str(j['image']),
    _str(j['drill']),
    _str(j['body']),
  );
  final String id, title;
  final String? subtitle, url, badge, image, drill, body;
}

class ProviderActionResult {
  ProviderActionResult(this.message, this.url);
  factory ProviderActionResult.fromJson(Map<String, dynamic> j) =>
      ProviderActionResult(j['message'] as String, _str(j['url']));
  final String message;
  final String? url;
}

// ── Everyday pages ───────────────────────────────────────────────────────────────────────────────────────────────────────

class ListEntry {
  ListEntry(this.id, this.list, this.text, this.done);
  factory ListEntry.fromJson(Map<String, dynamic> j) => ListEntry(
    j['id'] as String,
    j['list'] as String,
    j['text'] as String,
    j['done'] == true,
  );
  final String id, list, text;
  final bool done;
}

class ReminderItem {
  ReminderItem(this.id, this.text, this.remindAt);
  factory ReminderItem.fromJson(Map<String, dynamic> j) => ReminderItem(
    j['id'] as String,
    j['text'] as String,
    DateTime.tryParse((j['remindAt'] as String?) ?? '')?.toLocal(),
  );
  final String id, text;
  final DateTime? remindAt;
}

class AuditEvent {
  AuditEvent(this.id, this.action, this.detail, this.at);
  factory AuditEvent.fromJson(Map<String, dynamic> j) => AuditEvent(
    j['id'] as String,
    j['action'] as String,
    _str(j['detail']),
    DateTime.tryParse((j['occurredAt'] as String?) ?? '')?.toLocal(),
  );
  final String id, action;
  final String? detail;
  final DateTime? at;
}

/// A recent message in any connected chat app (the web app's notifications feed).
class ActivityEntry {
  ActivityEntry(this.app, this.chat, this.fromMe, this.text, this.at);
  factory ActivityEntry.fromJson(Map<String, dynamic> j) => ActivityEntry(
    (j['integrationKey'] as String?) ?? '',
    _str(j['chatName']) ?? '',
    j['fromMe'] == true,
    (j['text'] as String?) ?? '',
    DateTime.tryParse((j['timestamp'] as String?) ?? '')?.toLocal(),
  );
  final String app, chat, text;
  final bool fromMe;
  final DateTime? at;
}

class UserDetails {
  UserDetails(
    this.id,
    this.firstName,
    this.lastName,
    this.gender,
    this.dateOfBirth,
    this.email,
  );
  factory UserDetails.fromJson(Map<String, dynamic> j) => UserDetails(
    j['id'] as String,
    (j['firstName'] as String?) ?? '',
    (j['lastName'] as String?) ?? '',
    (j['gender'] as String?) ?? '',
    ((j['dateOfBirth'] as String?) ?? '').split('T').first,
    (j['email'] as String?) ?? '',
  );
  final String id, firstName, lastName, gender, dateOfBirth, email;
  String get fullName => '$firstName $lastName'.trim();
}

class MailSummary {
  MailSummary(
    this.id,
    this.from,
    this.subject,
    this.snippet,
    this.unread,
    this.date,
  );
  factory MailSummary.fromJson(Map<String, dynamic> j) => MailSummary(
    j['id'] as String,
    (j['from'] as String?) ?? '',
    (j['subject'] as String?) ?? '(no subject)',
    (j['snippet'] as String?) ?? '',
    j['isUnread'] == true,
    DateTime.tryParse((j['date'] as String?) ?? '')?.toLocal(),
  );
  final String id, from, subject, snippet;
  final bool unread;
  final DateTime? date;
}

class MailPage {
  MailPage(this.messages, this.nextPageToken);
  factory MailPage.fromJson(Map<String, dynamic> j) => MailPage(
    _list(j['messages']).map(MailSummary.fromJson).toList(),
    _str(j['nextPageToken']),
  );
  final List<MailSummary> messages;
  final String? nextPageToken;
}

class MailMessage {
  MailMessage(this.from, this.to, this.subject, this.date, this.body);
  factory MailMessage.fromJson(Map<String, dynamic> j) => MailMessage(
    (j['from'] as String?) ?? '',
    (j['to'] as String?) ?? '',
    (j['subject'] as String?) ?? '(no subject)',
    (j['date'] as String?) ?? '',
    (j['body'] as String?) ?? '',
  );
  final String from, to, subject, date, body;
}
