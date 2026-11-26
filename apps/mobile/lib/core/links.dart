import 'package:url_launcher/url_launcher.dart';

/// Where the web app lives. Sign-in pages of connected apps return here, so after approving in the browser the person comes back to the app.
const webUrl = String.fromEnvironment(
  'WEB_URL',
  defaultValue: 'https://qa.daksha.ampalab.com',
);

/// Opens a web address in the phone's browser. Only http(s) links are opened. Returns false when that was not possible.
typedef LinkOpener = Future<bool> Function(String url);

bool isWebLink(String url) {
  final u = Uri.tryParse(url);
  return u != null &&
      (u.scheme == 'https' || u.scheme == 'http') &&
      u.host.isNotEmpty;
}

Future<bool> openLink(String url) async {
  if (!isWebLink(url)) return false;
  return launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);
}
