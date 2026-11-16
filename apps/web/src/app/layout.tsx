import type { Metadata } from "next";
import "./globals.css";
import { themeInitScript } from "@/lib/theme";
export const metadata:Metadata={
 title:"Daksha — The Prajapati",
 description:"Daksha is your personal AI operator for conversations, tasks, reminders, email and calendar, under your control.",
 icons:{icon:"/daksha-lotus.svg",apple:"/daksha-lotus.svg"},
};
export default function RootLayout({children}:{children:React.ReactNode}){
 return <html lang="en" className="h-full antialiased" suppressHydrationWarning><head><script dangerouslySetInnerHTML={{__html:themeInitScript}}/></head><body className="flex min-h-full flex-col bg-canvas text-text">{children}</body></html>;
}
