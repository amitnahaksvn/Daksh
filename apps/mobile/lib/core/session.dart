import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'api_client.dart';
import 'providers.dart';

const _tokenKey = 'daksh.accessToken';

/// Whether the user is signed in. The app opens on the sign-in screen until this is true.
final sessionProvider = NotifierProvider<SessionNotifier, bool>(SessionNotifier.new);

class SessionNotifier extends Notifier<bool> {
  @override
  bool build() => ref.read(apiProvider).accessToken != null || devUser.isNotEmpty;

  /// Restores a saved sign-in. Called once before the app starts.
  static Future<String?> savedToken() async {
    try {
      return (await SharedPreferences.getInstance()).getString(_tokenKey);
    } catch (_) {
      return null;
    }
  }

  Future<void> signIn(String email, String password) async {
    final api = ref.read(apiProvider);
    final token = await api.login(email.trim(), password);
    api.accessToken = token;
    try {
      await (await SharedPreferences.getInstance()).setString(_tokenKey, token);
    } catch (_) {/* storage unavailable: the sign-in lasts until the app closes */}
    state = true;
  }

  Future<void> signOut() async {
    ref.read(apiProvider).accessToken = null;
    try {
      await (await SharedPreferences.getInstance()).remove(_tokenKey);
    } catch (_) {}
    state = false;
  }
}
