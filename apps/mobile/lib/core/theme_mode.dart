import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:shared_preferences/shared_preferences.dart';

final themeModeProvider = NotifierProvider<ThemeModeNotifier, ThemeMode>(ThemeModeNotifier.new);
final themeStyleProvider = NotifierProvider<ThemeStyleNotifier, String>(ThemeStyleNotifier.new);
final accentColorProvider = NotifierProvider<AccentColorNotifier, String>(AccentColorNotifier.new);

class ThemeModeNotifier extends Notifier<ThemeMode> {
 static const _key='daksh.themeMode';
 @override ThemeMode build(){_load();return ThemeMode.system;}
 Future<void> _load() async {try{final v=(await SharedPreferences.getInstance()).getString(_key);state=switch(v){'light'=>ThemeMode.light,'dark'=>ThemeMode.dark,_=>ThemeMode.system};}catch(_){}}
 Future<void> set(ThemeMode mode) async {state=mode;try{await(await SharedPreferences.getInstance()).setString(_key,mode.name);}catch(_){}}
}
class ThemeStyleNotifier extends Notifier<String> {
 static const _key='daksh.themeStyle';
 @override String build(){_load();return 'bento';}
 Future<void> _load() async {try{final v=(await SharedPreferences.getInstance()).getString(_key);state=v=='neural'||v=='aurora'?v!:'bento';}catch(_){}}
 Future<void> set(String value) async {if(!['bento','neural','aurora'].contains(value))return;state=value;try{await(await SharedPreferences.getInstance()).setString(_key,value);}catch(_){}}
}
class AccentColorNotifier extends Notifier<String> {
 static const _key='daksh.accentColor';
 static bool _valid(String value)=>RegExp(r'^#[0-9a-fA-F]{6}$').hasMatch(value)||['violet','blue','emerald','rose','amber'].contains(value);
 @override String build(){_load();return 'violet';}
 Future<void> _load() async {try{final v=(await SharedPreferences.getInstance()).getString(_key);state=v!=null&&_valid(v)?v:'violet';}catch(_){}}
 Future<void> set(String value) async {if(!_valid(value))return;state=value.toLowerCase();try{await(await SharedPreferences.getInstance()).setString(_key,state);}catch(_){}}
}
