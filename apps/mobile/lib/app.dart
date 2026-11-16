import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'core/theme.dart';
import 'core/theme_mode.dart';
import 'features/assistant_screen.dart';
import 'features/integrations_screen.dart';
import 'features/privacy_screen.dart';
import 'features/tasks_screen.dart';

const destinations = <(String, String, IconData, Widget)>[
 ('/assistant','Assistant',Icons.chat_bubble_outline,AssistantScreen()),
 ('/tasks','Tasks',Icons.checklist,TasksScreen()),
 ('/integrations','Integrations',Icons.extension_outlined,IntegrationsScreen()),
 ('/privacy','Privacy',Icons.lock_outline,PrivacyScreen()),
];
GoRouter buildRouter()=>GoRouter(initialLocation:'/assistant',routes:[
 ShellRoute(builder:(context,state,child){
  final index=destinations.indexWhere((d)=>state.uri.path.startsWith(d.$1));
  return Scaffold(
   appBar:AppBar(title:Text('Daksha · ${destinations[index<0?0:index].$2}'),actions:const[_AppearanceMenu(),SizedBox(width:8)]),
   body:child,
   bottomNavigationBar:NavigationBar(selectedIndex:index<0?0:index,onDestinationSelected:(i)=>context.go(destinations[i].$1),destinations:[for(final d in destinations)NavigationDestination(icon:Icon(d.$3),label:d.$2)]),
  );
 },routes:[for(final d in destinations)GoRoute(path:d.$1,builder:(_,_)=>d.$4)]),
]);

class _AppearanceMenu extends ConsumerWidget {
 const _AppearanceMenu();
 @override Widget build(BuildContext context,WidgetRef ref){
  final mode=ref.watch(themeModeProvider);
  final style=ref.watch(themeStyleProvider);
  final accent=ref.watch(accentColorProvider);
  return PopupMenuButton<String>(
   tooltip:'Appearance and colours',
   icon:const Icon(Icons.palette_outlined),
   onSelected:(value){
    final parts=value.split(':');
    if(parts.length!=2)return;
    switch(parts[0]){
     case 'mode': ref.read(themeModeProvider.notifier).set(ThemeMode.values.firstWhere((m)=>m.name==parts[1],orElse:()=>ThemeMode.system));
     case 'style': ref.read(themeStyleProvider.notifier).set(parts[1]);
     case 'accent': if(parts[1]=='custom'){_pickCustom(context,ref);}else{ref.read(accentColorProvider.notifier).set(parts[1]);}
    }
   },
   itemBuilder:(_)=>[
    const PopupMenuItem(enabled:false,child:Text('THEME STYLE')),
    CheckedPopupMenuItem(value:'style:bento',checked:style=='bento',child:const Text('Bento')),
    CheckedPopupMenuItem(value:'style:neural',checked:style=='neural',child:const Text('Neural')),
    CheckedPopupMenuItem(value:'style:aurora',checked:style=='aurora',child:const Text('Aurora')),
    const PopupMenuDivider(),
    const PopupMenuItem(enabled:false,child:Text('COLOUR PALETTE')),
    for(final p in const {'violet':'Violet','blue':'Ocean blue','emerald':'Emerald','rose':'Rose','amber':'Amber'}.entries)
     CheckedPopupMenuItem(value:'accent:${p.key}',checked:accent==p.key,child:Text(p.value)),
    const PopupMenuDivider(),
    const PopupMenuItem(enabled:false,child:Text('LIGHT MODE')),
    CheckedPopupMenuItem(value:'mode:system',checked:mode==ThemeMode.system,child:const Text('Follow device')),
    CheckedPopupMenuItem(value:'mode:light',checked:mode==ThemeMode.light,child:const Text('Day')),
    CheckedPopupMenuItem(value:'mode:dark',checked:mode==ThemeMode.dark,child:const Text('Night')),
   ],
  );
 }
}
class DakshApp extends ConsumerStatefulWidget{
 const DakshApp({super.key});
 @override ConsumerState<DakshApp> createState()=>_DakshAppState();
}
class _DakshAppState extends ConsumerState<DakshApp>{
 final _router=buildRouter();
 @override Widget build(BuildContext context){
  final style=ref.watch(themeStyleProvider);
  final accent=ref.watch(accentColorProvider);
  return MaterialApp.router(
   title:'Daksha — The Prajapati',
   theme:AppTheme.light(style:style,palette:accent),
   darkTheme:AppTheme.dark(style:style,palette:accent),
   themeMode:ref.watch(themeModeProvider),
   routerConfig:_router,
  );
 }
}
