import 'dart:convert';

import 'package:daksh_mobile/core/api_client.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

/// A stand-in for the Daksh API. Records every call, answers from [routes] ("GET /path" or "POST /path"), and 404s the rest.
class FakeServer {
  FakeServer(this.routes);
  final Map<String, Object? Function(http.Request req)> routes;
  final List<http.Request> calls = [];

  DakshApi get api => DakshApi(
        accessToken: 't',
        baseUrl: 'http://api.test',
        client: MockClient((req) async {
          calls.add(req);
          final key = '${req.method} ${req.url.path}';
          final handler = routes[key];
          if (handler == null) return http.Response(jsonEncode({'title': 'Not found: $key'}), 404);
          final body = handler(req);
          if (body is http.Response) return body;
          return http.Response(body == null ? '' : jsonEncode(body), 200);
        }),
      );

  List<http.Request> where(String method, String path) => calls.where((c) => c.method == method && c.url.path == path).toList();
  Map<String, dynamic> bodyOf(http.Request r) => jsonDecode(r.body) as Map<String, dynamic>;
}

Map<String, dynamic> spec({
  String key = 'youtube',
  String name = 'YouTube',
  String auth = 'oauth',
  bool hasWrite = true,
  bool writeNeedsSignIn = true,
  List<Map<String, dynamic>> connectFields = const [],
}) =>
    {
      'key': key,
      'name': name,
      'icon': 'SiYoutube',
      'color': '#FF0000',
      'account': 'Subscriptions and playlists',
      'connectTitle': 'Connect your $name account',
      'connectBlurb': 'Sign in to $name.',
      'connectLabel': 'Continue with $name',
      'writeLabel': 'Allow Daksh to manage playlists',
      'writeHint': 'Full access lets Daksha add videos.',
      'auth': auth,
      'writeNeedsSignIn': writeNeedsSignIn,
      'hasWrite': hasWrite,
      'setupDoc': 'Amit.md#$key',
      'connectFields': connectFields,
      'views': [
        {'id': 'playlists', 'label': 'Playlists', 'takesArg': false, 'hidden': false, 'dashboard': true, 'empty': null, 'inputs': []},
        {'id': 'playlist-items', 'label': 'Playlist', 'takesArg': true, 'hidden': true, 'dashboard': false, 'empty': null, 'inputs': []},
        {'id': 'search', 'label': 'Search', 'takesArg': false, 'hidden': false, 'dashboard': false, 'empty': null, 'inputs': [field('q', 'Search YouTube')]},
      ],
      'actions': [
        {'id': 'create-playlist', 'label': 'Create a playlist', 'hint': null, 'inputs': [field('title', 'Title')]},
      ],
      'settings': [
        {'key': 'dashboardView', 'label': 'What the dashboard tile shows', 'type': 'enum', 'hint': null, 'placeholder': null, 'options': ['playlists'], 'default': 'playlists'},
        {'key': 'itemCount', 'label': 'Items to show', 'type': 'count', 'hint': null, 'placeholder': null, 'options': null, 'default': '5'},
        {'key': 'pageName', 'label': 'Which Page', 'type': 'text', 'hint': null, 'placeholder': 'My business', 'options': null, 'default': ''},
      ],
    };

Map<String, dynamic> field(String key, String label, {String kind = 'text', bool required = true}) =>
    {'key': key, 'label': label, 'kind': kind, 'placeholder': null, 'required': required, 'options': null, 'optionsView': null, 'default': null, 'hint': null};

Map<String, dynamic> status({bool connected = true, bool write = false, bool configured = true, String? connectionId = 'c-1'}) => {
      'configured': configured,
      'connected': connected,
      'connectionId': connected ? connectionId : null,
      'accountId': 'me',
      'accountName': connected ? 'My channel' : null,
      'write': write,
      'scopes': '',
      'extra': {},
      'error': null,
    };

Map<String, dynamic> item(String id, String title, {String? subtitle, String? url, String? badge, String? drill, String? body}) =>
    {'id': id, 'title': title, 'subtitle': subtitle, 'url': url, 'badge': badge, 'image': null, 'drill': drill, 'body': body};
