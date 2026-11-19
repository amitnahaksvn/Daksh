import { AppShell } from "@/components/AppShell";
import { ToastHost } from "@/components/ToastHost";
import { ChatDock } from "@/components/ChatDock";
import { RequireAuth } from "@/components/RequireAuth";
export default function AppLayout({children}:{children:React.ReactNode}){
 return <RequireAuth><ToastHost/><ChatDock/><AppShell>{children}</AppShell></RequireAuth>;
}
