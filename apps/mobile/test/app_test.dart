import 'dart:convert';

import 'package:daksh_mobile/app.dart';
import 'package:daksh_mobile/core/api_client.dart';
import 'package:daksh_mobile/core/providers.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  signedOutTest();
  testWidgets('assistant screen sends a message and labels the simulated reply', (tester) async {
    final api = DakshApi(
      accessToken: 't',
      baseUrl: 'http://api.test',
      client: MockClient((_) async => http.Response(
          jsonEncode({'conversationId': 'c1', 'messageId': 'm1', 'reply': '[mock] hi', 'provider': 'mock', 'isSimulated': true}), 200)),
    );
    await tester.pumpWidget(ProviderScope(overrides: [apiProvider.overrideWithValue(api)], child: DakshApp()));
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(TextField), 'hello');
    await tester.pump();
    await tester.tap(find.byIcon(Icons.arrow_upward));
    await tester.pumpAndSettle();

    expect(find.textContaining('[mock] hi'), findsOneWidget);
    expect(find.textContaining('simulated reply'), findsOneWidget);
  });

  testWidgets('tasks screen shows an error with retry when the API is down', (tester) async {
    final api = DakshApi(accessToken: 't', baseUrl: 'http://x', client: MockClient((_) async => throw Exception('offline')));
    await tester.pumpWidget(ProviderScope(overrides: [apiProvider.overrideWithValue(api)], child: DakshApp()));
    await tester.pumpAndSettle();
    await tester.tap(find.byTooltip('Open navigation menu'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Tasks').last);
    await tester.pumpAndSettle();

    expect(find.textContaining('Cannot reach the Daksh API'), findsOneWidget);
    expect(find.text('Try again'), findsOneWidget);
  });
}

// Signed-out users land on the sign-in screen, with no landing page in between.
void signedOutTest() {
  testWidgets('opens on sign-in when signed out', (tester) async {
    final api = DakshApi(baseUrl: 'http://x', client: MockClient((_) async => throw Exception('offline')));
    await tester.pumpWidget(ProviderScope(overrides: [apiProvider.overrideWithValue(api)], child: DakshApp()));
    await tester.pumpAndSettle();
    expect(find.text('Sign in'), findsWidgets);
    expect(find.text('Sign in to your assistant'), findsOneWidget);
    expect(find.text('Dashboard'), findsNothing);
  });
}
