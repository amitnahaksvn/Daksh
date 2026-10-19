import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:logging/logging.dart';

import 'app.dart';

void main() {
  Logger.root.level = kReleaseMode ? Level.INFO : Level.ALL;
  Logger.root.onRecord.listen((r) => debugPrint('${r.level.name} ${r.loggerName}: ${r.message}'));
  FlutterError.onError = (d) {
    Logger('flutter').severe('Uncaught Flutter error', d.exception, d.stack);
    FlutterError.presentError(d);
  };
  PlatformDispatcher.instance.onError = (e, st) {
    Logger('platform').severe('Uncaught platform error', e, st);
    return true;
  };
  runApp(ProviderScope(child: DakshApp()));
}
