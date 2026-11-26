import 'dart:convert';

import 'package:daksh_mobile/core/api_client.dart';
import 'package:flutter_test/flutter_test.dart';

import 'fake_server.dart';

void main() {
  test('a provider app describes itself: tabs hide drill-down lists, required inputs are known', () async {
    final s = FakeServer({'GET /api/integrations/youtube/spec': (_) => spec()});
    final parsed = await s.api.providerSpec('youtube');
    expect(parsed.name, 'YouTube');
    expect(parsed.tabs.map((v) => v.label), ['Playlists', 'Search']); // "Playlist" is reached by drilling in
    expect(parsed.views.firstWhere((v) => v.id == 'search').needsInput, isTrue);
    expect(parsed.views.firstWhere((v) => v.id == 'playlists').needsInput, isFalse);
    expect(parsed.actions.single.label, 'Create a playlist');
    expect(parsed.settings.map((x) => x.key), ['dashboardView', 'itemCount', 'pageName']);
    expect(parsed.isOAuth, isTrue);
  });

  test('lists are read with their query values escaped', () async {
    final s = FakeServer({'GET /api/integrations/youtube/views/search': (_) => [item('v1', 'A video', badge: '1k views')]});
    final rows = await s.api.providerView('youtube', 'search', {'q': 'a&b c'});
    expect(rows.single.badge, '1k views');
    expect(s.calls.single.url.query, 'q=a%26b+c');
  });

  test('credentials are posted as JSON; OAuth returns the address to open', () async {
    final s = FakeServer({
      'POST /api/integrations/home-assistant/connect': (_) => null,
      'POST /api/integrations/x/connect': (_) => {'authorizationUrl': 'https://x.com/i/oauth2/authorize?state=1'},
    });
    await s.api.connectWithDetails('home-assistant', {'url': 'http://ha.local:8123', 'token': 'T'});
    expect(s.bodyOf(s.calls.first), {'url': 'http://ha.local:8123', 'token': 'T'});
    expect(await s.api.oauthUrl('x', {'write': 'true'}), 'https://x.com/i/oauth2/authorize?state=1');
    expect(s.calls.last.url.query, 'write=true');
  });

  test('actions, settings and the full-access switch use the shared app endpoints', () async {
    final s = FakeServer({
      'POST /api/integrations/youtube/actions/create-playlist': (_) => {'message': 'Playlist created.', 'url': 'https://www.youtube.com/playlist?list=PL9'},
      'PUT /api/integrations/youtube/settings': (_) => {'itemCount': 8, 'showOnDashboard': false},
      'POST /api/integrations/youtube/write': (_) => null,
      'POST /api/integrations/youtube/disconnect': (_) => null,
    });
    final r = await s.api.providerAction('youtube', 'create-playlist', {'title': 'Trip'});
    expect(r.message, 'Playlist created.');
    expect(s.bodyOf(s.calls.first), {'inputs': {'title': 'Trip'}});
    expect((await s.api.updateAppSettings('youtube', {'itemCount': 8}))['itemCount'], 8);
    await s.api.setAppWrite('youtube', true);
    expect(s.bodyOf(s.calls[2]), {'enabled': true});
    await s.api.disconnectApp('youtube');
    expect(s.calls.last.method, 'POST');
  });

  test('provider statuses, the catalogue and the app dashboard are read', () async {
    final s = FakeServer({
      'GET /api/integrations/provider-apps/statuses': (_) => {'youtube': true, 'shopify': false},
      'GET /api/integrations': (_) => [{'integrationKey': 'youtube', 'name': 'YouTube', 'description': 'Videos', 'category': 'Social', 'color': '#FF0000', 'isEnabled': true}],
      'GET /api/integrations/youtube/dashboard': (_) => {'connected': true, 'account': 'Me', 'sections': [{'title': 'Playlists', 'href': null, 'items': [{'id': '1', 'title': 'Focus', 'subtitle': '3 videos', 'url': null, 'badge': null}]}], 'errors': []},
    });
    expect(await s.api.providerStatuses(), {'youtube': true, 'shopify': false});
    expect((await s.api.appCatalog()).single.isEnabled, isTrue);
    final d = await s.api.appDashboard('youtube');
    expect(d.sections.single.items.single.title, 'Focus');
  });

  test('lists, reminders, mail and the profile use the same endpoints as the website', () async {
    final s = FakeServer({
      'GET /api/v1/lists': (_) => [{'id': 'l1', 'list': 'Shopping', 'text': 'Milk', 'done': false, 'createdAt': '2026-01-01T00:00:00Z'}],
      'POST /api/v1/lists/l1/toggle': (_) => null,
      'GET /api/v1/reminders': (_) => [{'id': 'r1', 'text': 'Call mom', 'remindAt': '2026-12-01T10:00:00Z'}],
      'GET /api/integrations/gmail/messages': (_) => {'messages': [{'id': 'm1', 'from': 'a@b.co', 'subject': 'Hi', 'snippet': 'Hello', 'isUnread': true, 'date': '2026-12-01T10:00:00Z'}], 'nextPageToken': null},
      'GET /api/v1/account/me': (_) => {'id': 'u', 'firstName': 'Amit', 'lastName': 'N', 'gender': 'Male', 'dateOfBirth': '1990-01-02T00:00:00Z', 'email': 'a@b.co', 'createdAtUtc': '2026-01-01T00:00:00Z'},
      'POST /api/v1/account/me/password': (_) => null,
    });
    expect((await s.api.lists()).single.text, 'Milk');
    await s.api.toggleListItem('l1');
    expect((await s.api.reminders()).single.text, 'Call mom');
    final page = await s.api.mail('in:inbox');
    expect(page.messages.single.unread, isTrue);
    expect(s.calls.last.url.query, 'q=in%3Ainbox');
    final me = await s.api.me();
    expect(me.fullName, 'Amit N');
    expect(me.dateOfBirth, '1990-01-02');
    await s.api.changePassword('old', 'new');
    expect(jsonDecode(s.calls.last.body), {'currentPassword': 'old', 'newPassword': 'new'});
  });

  test('an API failure says why', () async {
    final s = FakeServer({'GET /api/integrations/youtube/views/playlists': (_) => throw ApiException(409, 'Reconnect YouTube.')});
    expect(s.api.providerView('youtube', 'playlists'), throwsA(isA<Exception>()));
  });
}
