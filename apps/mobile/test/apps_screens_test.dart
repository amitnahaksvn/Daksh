import 'package:daksh_mobile/core/providers.dart';
import 'package:daksh_mobile/features/apps_screen.dart';
import 'package:daksh_mobile/features/email_screen.dart';
import 'package:daksh_mobile/features/lists_screen.dart';
import 'package:daksh_mobile/features/profile_screen.dart';
import 'package:daksh_mobile/features/provider_app_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

import 'fake_server.dart';

Future<List<String>> pumpWith(WidgetTester tester, FakeServer server, Widget home, {Map<String, Widget Function(GoRouterState)> extra = const {}}) async {
  final opened = <String>[];
  final router = GoRouter(routes: [
    GoRoute(path: '/', builder: (_, _) => Scaffold(body: home)),
    GoRoute(path: '/apps/provider/:key', builder: (_, s) => ProviderAppScreen(appKey: s.pathParameters['key']!)),
    for (final e in extra.entries) GoRoute(path: e.key, builder: (_, s) => e.value(s)),
  ]);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      apiProvider.overrideWithValue(server.api),
      linkOpenerProvider.overrideWithValue((url) async { opened.add(url); return true; }),
    ],
    child: MaterialApp.router(routerConfig: router),
  ));
  await tester.pumpAndSettle();
  return opened;
}

FakeServer youtubeServer({bool connected = true, bool write = false, Map<String, dynamic>? specOverride}) => FakeServer({
      'GET /api/integrations/youtube/spec': (_) => specOverride ?? spec(),
      'GET /api/integrations/youtube/status': (_) => status(connected: connected, write: write),
      'GET /api/integrations/youtube/views/playlists': (_) => [item('PL1', 'Focus music', subtitle: '12 videos', url: 'https://www.youtube.com/playlist?list=PL1', drill: 'playlist-items')],
      'GET /api/integrations/youtube/views/playlist-items': (_) => [item('v1', 'Song one', subtitle: 'Artist')],
      'GET /api/integrations/youtube/views/search': (r) => [item('s1', 'Result for ${r.url.queryParameters['q']}', url: 'https://www.youtube.com/watch?v=abc')],
      'POST /api/integrations/youtube/connect': (_) => {'authorizationUrl': 'https://accounts.google.com/o/oauth2/v2/auth?state=1'},
      'GET /api/integrations/youtube/settings': (_) => {'dashboardView': 'playlists', 'itemCount': 5, 'pageName': '', 'showOnDashboard': false},
      'PUT /api/integrations/youtube/settings': (r) => {'dashboardView': 'playlists', 'itemCount': 8, 'pageName': '', 'showOnDashboard': false},
      'POST /api/integrations/youtube/write': (_) => null,
      'POST /api/integrations/youtube/disconnect': (_) => null,
      'GET /api/dashboard/apps/available': (_) => [{'connectionId': 'c-1', 'integrationKey': 'youtube', 'name': 'YouTube', 'accountIdentifier': 'My channel', 'dashboardId': null}],
      'POST /api/dashboard/apps': (_) => null,
      'GET /api/dashboard/apps': (_) => [],
      'POST /api/integrations/youtube/actions/create-playlist': (_) => {'message': 'Playlist created.', 'url': 'https://www.youtube.com/playlist?list=PL9'},
    });

void main() {
  group('Apps catalogue', () {
    FakeServer catalogue() => FakeServer({
          'GET /api/integrations': (_) => [
                {'integrationKey': 'youtube', 'name': 'YouTube', 'description': 'Videos and playlists', 'category': 'Social', 'color': '#FF0000', 'isEnabled': true},
                {'integrationKey': 'shopify', 'name': 'Shopify', 'description': 'Your store', 'category': 'Commerce', 'color': '#7AB55C', 'isEnabled': true},
                {'integrationKey': 'tuya', 'name': 'Tuya', 'description': 'Smart devices', 'category': 'Smart home', 'color': '#17D4D4', 'isEnabled': false},
              ],
          'GET /api/integrations/provider-apps/statuses': (_) => {'youtube': true, 'shopify': false},
          'GET /api/integrations/shopify/spec': (_) => spec(key: 'shopify', name: 'Shopify'),
          'GET /api/integrations/shopify/status': (_) => status(connected: false),
        });

    testWidgets('lists every app with its state, connected ones first', (tester) async {
      await pumpWith(tester, catalogue(), const AppsScreen());
      expect(find.text('YouTube'), findsOneWidget);
      expect(find.text('Connected'), findsOneWidget);
      expect(find.text('Connect'), findsOneWidget); // Shopify
      expect(find.text('Coming soon'), findsOneWidget); // Tuya
      expect(find.text('2 of 3 apps work today'), findsOneWidget);
      expect(tester.getTopLeft(find.text('YouTube')).dy, lessThan(tester.getTopLeft(find.text('Shopify')).dy));
    });

    testWidgets('search narrows the list and a category chip filters it', (tester) async {
      await pumpWith(tester, catalogue(), const AppsScreen());
      await tester.enterText(find.byType(TextField), 'shop');
      await tester.pumpAndSettle();
      expect(find.text('Shopify'), findsOneWidget);
      expect(find.text('YouTube'), findsNothing);
      await tester.enterText(find.byType(TextField), '');
      await tester.tap(find.widgetWithText(ChoiceChip, 'Smart home'));
      await tester.pumpAndSettle();
      expect(find.text('Tuya'), findsOneWidget);
      expect(find.text('YouTube'), findsNothing);
    });

    testWidgets('an app that is not available yet says so instead of opening', (tester) async {
      await pumpWith(tester, catalogue(), const AppsScreen());
      await tester.tap(find.text('Tuya'));
      await tester.pump();
      expect(find.text('Tuya is not available yet.'), findsOneWidget);
    });

    testWidgets('tapping an app opens its own screen', (tester) async {
      await pumpWith(tester, catalogue(), const AppsScreen());
      await tester.tap(find.text('Shopify'));
      await tester.pumpAndSettle();
      expect(find.text('Connect your Shopify account'), findsOneWidget);
    });
  });

  group('A connected provider app', () {
    testWidgets('shows a tab per list and a Do something tab, but not the drill-down list', (tester) async {
      await pumpWith(tester, youtubeServer(), const ProviderAppScreen(appKey: 'youtube'));
      for (final t in ['Playlists', 'Search', 'Do something']) {
        expect(find.widgetWithText(Tab, t), findsOneWidget);
      }
      expect(find.widgetWithText(Tab, 'Playlist'), findsNothing);
      expect(find.text('My channel · read-only'), findsOneWidget);
      expect(find.text('Focus music'), findsOneWidget);
    });

    testWidgets('a row with more inside drills in', (tester) async {
      final s = youtubeServer();
      await pumpWith(tester, s, const ProviderAppScreen(appKey: 'youtube'));
      await tester.tap(find.text('Focus music'));
      await tester.pumpAndSettle();
      expect(find.text('Song one'), findsOneWidget);
      expect(s.where('GET', '/api/integrations/youtube/views/playlist-items').single.url.queryParameters['arg'], 'PL1');
    });

    testWidgets('a list that needs input waits for it, then searches', (tester) async {
      final s = youtubeServer();
      final opened = await pumpWith(tester, s, const ProviderAppScreen(appKey: 'youtube'));
      await tester.tap(find.widgetWithText(Tab, 'Search'));
      await tester.pumpAndSettle();
      expect(find.text('Fill in the details above to see results.'), findsOneWidget);
      expect(s.where('GET', '/api/integrations/youtube/views/search'), isEmpty);
      await tester.enterText(find.widgetWithText(TextField, 'Search YouTube'), 'lofi');
      await tester.tap(find.text('Show'));
      await tester.pumpAndSettle();
      expect(find.text('Result for lofi'), findsOneWidget);
      await tester.tap(find.text('Result for lofi'));
      expect(opened, ['https://www.youtube.com/watch?v=abc']); // opens in the browser
    });

    testWidgets('actions are switched off while read-only and run with full access', (tester) async {
      final ro = youtubeServer(write: false);
      await pumpWith(tester, ro, const ProviderAppScreen(appKey: 'youtube'));
      await tester.tap(find.widgetWithText(Tab, 'Do something'));
      await tester.pumpAndSettle();
      expect(find.textContaining('read-only access'), findsOneWidget);
      await tester.tap(find.text('Create a playlist'));
      await tester.pumpAndSettle();
      expect(tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Create a playlist')).onPressed, isNull);

      final rw = youtubeServer(write: true);
      await pumpWith(tester, rw, const ProviderAppScreen(appKey: 'youtube'));
      await tester.tap(find.widgetWithText(Tab, 'Do something'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Create a playlist'));
      await tester.pumpAndSettle();
      await tester.enterText(find.widgetWithText(TextField, 'Title'), 'Road trip');
      await tester.tap(find.widgetWithText(FilledButton, 'Create a playlist'));
      await tester.pumpAndSettle();
      expect(find.text('Playlist created.'), findsOneWidget);
      expect(rw.bodyOf(rw.where('POST', '/api/integrations/youtube/actions/create-playlist').single), {'inputs': {'title': 'Road trip'}});
    });
  });

  group('Manage', () {
    testWidgets('offers General and Settings, with full access, dashboard and disconnect', (tester) async {
      await pumpWith(tester, youtubeServer(), const ProviderAppScreen(appKey: 'youtube'));
      await tester.tap(find.byTooltip('Manage'));
      await tester.pumpAndSettle();
      expect(find.widgetWithText(Tab, 'General'), findsOneWidget);
      expect(find.widgetWithText(Tab, 'Settings'), findsOneWidget);
      expect(find.text('Allow Daksh to manage playlists'), findsOneWidget);
      expect(find.text('Show YouTube on dashboard'), findsOneWidget);
      expect(find.text('Disconnect'), findsOneWidget);
    });

    testWidgets('turning on full access asks the provider to approve it (opens the browser)', (tester) async {
      final s = youtubeServer();
      final opened = await pumpWith(tester, s, const ProviderAppScreen(appKey: 'youtube'));
      await tester.tap(find.byTooltip('Manage'));
      await tester.pumpAndSettle();
      await tester.tap(find.byType(Switch).first);
      await tester.pumpAndSettle();
      expect(opened.single, startsWith('https://accounts.google.com/'));
      expect(s.where('GET', '/api/integrations/youtube/status'), isNotEmpty);
      expect(s.where('POST', '/api/integrations/youtube/connect').single.url.queryParameters['write'], 'true');
    });

    testWidgets('full access that needs no new approval is just a switch', (tester) async {
      final s = youtubeServer(specOverride: spec(writeNeedsSignIn: false));
      await pumpWith(tester, s, const ProviderAppScreen(appKey: 'youtube'));
      await tester.tap(find.byTooltip('Manage'));
      await tester.pumpAndSettle();
      await tester.tap(find.byType(Switch).first);
      await tester.pumpAndSettle();
      expect(s.bodyOf(s.where('POST', '/api/integrations/youtube/write').single), {'enabled': true});
      expect(find.text('Full access is on'), findsOneWidget);
    });

    testWidgets('read-only apps do not offer the switch', (tester) async {
      await pumpWith(tester, youtubeServer(specOverride: spec(hasWrite: false)), const ProviderAppScreen(appKey: 'youtube'));
      await tester.tap(find.byTooltip('Manage'));
      await tester.pumpAndSettle();
      expect(find.text('YouTube is read-only'), findsOneWidget);
      expect(find.text('Allow Daksh to manage playlists'), findsNothing);
    });

    testWidgets('settings save and the dashboard switch adds the tile', (tester) async {
      final s = youtubeServer();
      await pumpWith(tester, s, const ProviderAppScreen(appKey: 'youtube'));
      await tester.tap(find.byTooltip('Manage'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Show YouTube on dashboard'));
      await tester.pumpAndSettle();
      expect(s.bodyOf(s.where('POST', '/api/dashboard/apps').single), {'connectionId': 'c-1'});
      await tester.tap(find.widgetWithText(Tab, 'Settings'));
      await tester.pumpAndSettle();
      expect(find.text('Items to show'), findsOneWidget);
      await tester.tap(find.byType(DropdownButton<int>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('8').last);
      await tester.pumpAndSettle();
      expect(s.bodyOf(s.where('PUT', '/api/integrations/youtube/settings').single), {'itemCount': 8});
    });

    testWidgets('disconnect asks first', (tester) async {
      final s = youtubeServer();
      await pumpWith(tester, s, const ProviderAppScreen(appKey: 'youtube'));
      await tester.tap(find.byTooltip('Manage'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Disconnect'));
      await tester.pumpAndSettle();
      expect(find.text('Disconnect YouTube?'), findsOneWidget);
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();
      expect(s.where('POST', '/api/integrations/youtube/disconnect'), isEmpty);
      await tester.tap(find.text('Disconnect'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Disconnect'));
      await tester.pumpAndSettle();
      expect(s.where('POST', '/api/integrations/youtube/disconnect'), hasLength(1));
    });
  });

  group('Connecting', () {
    testWidgets('an OAuth app opens the browser and waits for you to come back', (tester) async {
      final s = youtubeServer(connected: false);
      final opened = await pumpWith(tester, s, const ProviderAppScreen(appKey: 'youtube'));
      expect(find.text('Connect your YouTube account'), findsOneWidget);
      await tester.tap(find.text('Continue with YouTube'));
      await tester.pumpAndSettle();
      expect(opened.single, 'https://accounts.google.com/o/oauth2/v2/auth?state=1');
      expect(find.text('Finish signing in in your browser, then come back here.'), findsOneWidget);
      expect(s.where('POST', '/api/integrations/youtube/connect').single.url.queryParameters['write'], 'false');
    });

    testWidgets('the optional full-access box is passed on', (tester) async {
      final s = youtubeServer(connected: false);
      await pumpWith(tester, s, const ProviderAppScreen(appKey: 'youtube'));
      await tester.tap(find.byType(Checkbox));
      await tester.tap(find.text('Continue with YouTube'));
      await tester.pumpAndSettle();
      expect(s.where('POST', '/api/integrations/youtube/connect').single.url.queryParameters['write'], 'true');
    });

    testWidgets('a credential app asks for its details and posts them', (tester) async {
      final ha = spec(key: 'home-assistant', name: 'Home Assistant', auth: 'credentials', writeNeedsSignIn: false, connectFields: [
        field('url', 'Home Assistant address', kind: 'url'),
        field('token', 'Long-lived access token', kind: 'secret'),
      ]);
      final s = FakeServer({
        'GET /api/integrations/home-assistant/spec': (_) => ha,
        'GET /api/integrations/home-assistant/status': (_) => status(connected: false),
        'POST /api/integrations/home-assistant/connect': (_) => null,
      });
      await pumpWith(tester, s, const ProviderAppScreen(appKey: 'home-assistant'));
      expect(tester.widget<TextField>(find.widgetWithText(TextField, 'Long-lived access token')).obscureText, isTrue);
      await tester.enterText(find.widgetWithText(TextField, 'Home Assistant address'), 'http://ha.local:8123');
      await tester.enterText(find.widgetWithText(TextField, 'Long-lived access token'), 'TOKEN');
      await tester.tap(find.text('Continue with Home Assistant'));
      await tester.pumpAndSettle();
      expect(s.bodyOf(s.where('POST', '/api/integrations/home-assistant/connect').single), {'url': 'http://ha.local:8123', 'token': 'TOKEN'});
    });

    testWidgets('an app whose keys are not on the server says so', (tester) async {
      final s = FakeServer({
        'GET /api/integrations/youtube/spec': (_) => spec(),
        'GET /api/integrations/youtube/status': (_) => status(connected: false, configured: false),
      });
      await pumpWith(tester, s, const ProviderAppScreen(appKey: 'youtube'));
      expect(find.textContaining('not set up on the server yet'), findsOneWidget);
      expect(find.textContaining('Amit.md#youtube'), findsOneWidget);
    });
  });

  group('Everyday pages', () {
    testWidgets('lists group their items and tick them off', (tester) async {
      final s = FakeServer({
        'GET /api/v1/lists': (_) => [
              {'id': 'l1', 'list': 'Shopping', 'text': 'Milk', 'done': false, 'createdAt': ''},
              {'id': 'l2', 'list': 'Shopping', 'text': 'Eggs', 'done': true, 'createdAt': ''},
            ],
        'POST /api/v1/lists/l1/toggle': (_) => null,
      });
      await pumpWith(tester, s, const ListsScreen());
      expect(find.text('Shopping'), findsOneWidget);
      await tester.tap(find.text('Milk'));
      await tester.pumpAndSettle();
      expect(s.where('POST', '/api/v1/lists/l1/toggle'), hasLength(1));
    });

    testWidgets('email shows the inbox and opens a message', (tester) async {
      final s = FakeServer({
        'GET /api/integrations/gmail/messages': (_) => {'messages': [{'id': 'm1', 'from': 'a@b.co', 'subject': 'Hello there', 'snippet': 'Preview', 'isUnread': true, 'date': null}], 'nextPageToken': null},
        'GET /api/integrations/gmail/messages/m1': (_) => {'id': 'm1', 'from': 'a@b.co', 'to': 'me@x.co', 'subject': 'Hello there', 'date': 'Mon', 'body': 'Full message text', 'html': null},
      });
      await pumpWith(tester, s, const EmailScreen());
      expect(find.text('Hello there'), findsOneWidget);
      await tester.tap(find.text('Hello there'));
      await tester.pumpAndSettle();
      expect(find.text('Full message text'), findsOneWidget);
    });

    testWidgets('the profile shows who you are and the busy switch', (tester) async {
      final s = FakeServer({
        'GET /api/v1/account/me': (_) => {'id': 'u', 'firstName': 'Amit', 'lastName': 'Nahak', 'gender': 'Male', 'dateOfBirth': '1990-01-02T00:00:00Z', 'email': 'a@b.co', 'createdAtUtc': ''},
        'GET /api/v1/account/me/busy': (_) => {'enabled': false, 'isBusyNow': false, 'timezone': 'Asia/Kolkata'},
        'PUT /api/v1/account/me/busy': (r) => {'enabled': true, 'isBusyNow': true, 'timezone': 'Asia/Kolkata'},
      });
      await pumpWith(tester, s, const ProfileScreen());
      expect(find.text('Amit Nahak'), findsOneWidget);
      await tester.tap(find.byType(Switch));
      await tester.pumpAndSettle();
      final body = s.bodyOf(s.where('PUT', '/api/v1/account/me/busy').single);
      expect(body['enabled'], isTrue);
      expect(body.containsKey('isBusyNow'), isFalse); // read-only field is not sent back
    });
  });
}
