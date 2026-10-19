import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'api_client.dart';

final apiProvider = Provider<DakshApi>((ref) => DakshApi());

final tasksProvider = FutureProvider.autoDispose<List<TaskItem>>((ref) => ref.watch(apiProvider).tasks());
final integrationsProvider =
    FutureProvider.autoDispose<List<IntegrationStatus>>((ref) => ref.watch(apiProvider).integrations());
final privacyProvider = FutureProvider.autoDispose<PrivacySettings>((ref) => ref.watch(apiProvider).privacy());
