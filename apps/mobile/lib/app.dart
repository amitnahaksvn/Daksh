import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'core/session.dart';
import 'core/theme.dart';
import 'features/assistant_screen.dart';
import 'features/chats_screen.dart';
import 'features/dashboard_screen.dart';
import 'features/integrations_screen.dart';
import 'features/login_screen.dart';
import 'features/privacy_screen.dart';
import 'features/calendar_screen.dart';
import 'features/tasks_screen.dart';

/// Opens on sign-in (no landing page); once signed in, straight to the chat. Other pages open over it with a back arrow.
GoRouter buildRouter(Listenable signedInChanges,bool Function() signedIn)=>GoRouter(
 initialLocation:'/',
 refreshListenable:signedInChanges,
 redirect:(context,state){
  final onLogin=state.uri.path=='/login';
  if(!signedIn())return onLogin?null:'/login';
  return onLogin?'/':null;
 },
 routes:[
 GoRoute(path:'/login',builder:(_,_)=>const LoginScreen()),
 GoRoute(path:'/',builder:(_,_)=>const AssistantScreen()),
 GoRoute(path:'/dashboard',builder:(_,_)=>const _Page('Dashboard',DashboardScreen())),
 GoRoute(path:'/chats',builder:(_,_)=>const _Page('Chats',ChatsScreen())),
 GoRoute(path:'/calendar',builder:(_,_)=>const _Page('Calendar',CalendarScreen())),
 GoRoute(path:'/tasks',builder:(_,_)=>const _Page('Tasks',TasksScreen())),
 GoRoute(path:'/more/integrations',builder:(_,_)=>const _Page('Integrations',IntegrationsScreen())),
 GoRoute(path:'/more/privacy',builder:(_,_)=>const _Page('Privacy',PrivacyScreen())),
]);

class _Page extends StatelessWidget {
 const _Page(this.title,this.child);
 final String title;
 final Widget child;
 @override Widget build(BuildContext context)=>Scaffold(appBar:AppBar(title:Text(title)),body:child);
}

class DakshApp extends ConsumerStatefulWidget{
 const DakshApp({super.key});
 @override ConsumerState<DakshApp> createState()=>_DakshAppState();
}
class _DakshAppState extends ConsumerState<DakshApp>{
 final _signedIn=ValueNotifier<bool>(false);
 late final GoRouter _router;
 @override void initState(){
  super.initState();
  _signedIn.value=ref.read(sessionProvider);
  _router=buildRouter(_signedIn,()=>_signedIn.value);
 }
 @override void dispose(){_router.dispose();_signedIn.dispose();super.dispose();}
 @override Widget build(BuildContext context){
  ref.listen<bool>(sessionProvider,(_,v)=>_signedIn.value=v);
  return MaterialApp.router(
   title:'Daksha — The Prajapati',
   theme:AppTheme.light(style:'sandhya',palette:'sandhya'),
   darkTheme:AppTheme.dark(style:'sandhya',palette:'sandhya'),
   themeMode:ThemeMode.system, // the website's look, light or dark with the device; no colour choices
   routerConfig:_router,
  );
 }
}
