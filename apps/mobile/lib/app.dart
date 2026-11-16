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
 Future<void> _chooseCustom(BuildContext context, WidgetRef ref) async {
  final current=ref.read(accentColorProvider);
  final controller=TextEditingController(text:RegExp(r'^#[0-9a-fA-F]{6}$').hasMatch(current)?current:'#8B5CF6');
  final selected=await showDialog<String>(context:context,builder:(context)=>AlertDialog(
   title:const Text('Custom accent colour'),
   content:TextField(controller:controller,autocorrect:false,enableSuggestions:false,maxLength:7,
    decoration:const InputDecoration(labelText:'HEX colour',hintText:'#8B5CF6',helperText:'Enter a six-digit value such as #8B5CF6'),
   ),
   actions:[TextButton(onPressed:()=>Navigator.pop(context),child:const Text('Cancel')),
    FilledButton(onPressed:(){final value=controller.text.trim();if(RegExp(r'^#[0-9a-fA-F]{6}$').hasMatch(value)){Navigator.pop(context,value);}else{ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content:Text('Enter a valid HEX value such as #8B5CF6')));}},child:const Text('Apply'))],
  ));
  controller.dispose();
  if(selected!=null) await ref.read(accentColorProvider.notifier).set(selected);
 }
 @override Widget build(BuildContext context,WidgetRef ref){
  final mode=ref.watch(themeModeProvider);
  final style=ref.watch(themeStyleProvider);
  final accent=ref.watch(accentColorProvider);
  final presets={'violet':'Violet','blue':'Ocean blue','emerald':'Emerald','rose':'Rose','amber':'Amber'};
  return PopupMenuButton<String>(
   tooltip:'Appearance and colours',
   icon:const Icon(Icons.palette_outlined),
   onSelected:(value) async {
    final parts=value.split(':');
    if(parts.length!=2)return;
    switch(parts[0]){
     case 'mode': await ref.read(themeModeProvider.notifier).set(ThemeMode.values.firstWhere((m)=>m.name==parts[1],orElse:()=>ThemeMode.system));
     case 'style': await ref.read(themeStyleProvider.notifier).set(parts[1]);
     case 'accent': await ref.read(accentColorProvider.notifier).set(parts[1]);
     case 'custom': await _chooseCustom(context,ref);
    }
   },
   itemBuilder:(_)=>[
    const PopupMenuItem(enabled:false,child:Text('THEME STYLE')),
    CheckedPopupMenuItem(value:'style:bento',checked:style=='bento',child:const Text('Bento')),
    CheckedPopupMenuItem(value:'style:neural',checked:style=='neural',child:const Text('Neural')),
    CheckedPopupMenuItem(value:'style:aurora',checked:style=='aurora',child:const Text('Aurora')),
    const PopupMenuDivider(),
    const PopupMenuItem(enabled:false,child:Text('COLOUR PALETTE')),
    for(final p in presets.entries)
     CheckedPopupMenuItem(value:'accent:${p.key}',checked:accent==p.key,child:Text(p.value)),
    PopupMenuItem(value:'custom:hex',child:Row(children:[const Icon(Icons.color_lens_outlined,size:18),const SizedBox(width:10),Text(RegExp(r'^#[0-9a-fA-F]{6}$').hasMatch(accent)?'Custom · $accent':'Choose any HEX colour')])),
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
