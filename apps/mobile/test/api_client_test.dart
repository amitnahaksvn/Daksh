import 'dart:convert';

import 'package:daksh_mobile/core/api_client.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  test('sendMessage posts to the assistant endpoint and parses the reply', () async {
    late http.Request captured;
    final api = DakshApi(
      baseUrl: 'http://api.test',
      client: MockClient((req) async {
        captured = req;
        return http.Response(
            jsonEncode({'conversationId': 'c1', 'messageId': 'm1', 'reply': '[mock] hi', 'provider': 'mock', 'isSimulated': true}), 200);
      }),
    );
    final r = await api.sendMessage('hello');
    expect(captured.url.toString(), 'http://api.test/api/v1/assistant/messages');
    expect(jsonDecode(captured.body)['message'], 'hello');
    expect(r.isSimulated, isTrue);
  });

  test('validation problems become ApiException with field errors', () async {
    final api = DakshApi(
      baseUrl: 'http://api.test',
      client: MockClient((_) async => http.Response(
          jsonEncode({'title': 'Validation failed', 'errors': {'Message': ['must not be empty']}}), 400)),
    );
    expect(
      api.sendMessage(''),
      throwsA(isA<ApiException>()
          .having((e) => e.statusCode, 'status', 400)
          .having((e) => e.fieldErrors['Message'], 'field', ['must not be empty'])),
    );
  });

  test('401 produces a sign-in message and network failure status 0', () async {
    final unauth = DakshApi(baseUrl: 'http://x', client: MockClient((_) async => http.Response('', 401)));
    expect(unauth.tasks(), throwsA(isA<ApiException>().having((e) => e.message, 'msg', contains('sign in'))));

    final down = DakshApi(baseUrl: 'http://x', client: MockClient((_) async => throw Exception('offline')));
    expect(down.tasks(), throwsA(isA<ApiException>().having((e) => e.statusCode, 'status', 0)));
  });
}
