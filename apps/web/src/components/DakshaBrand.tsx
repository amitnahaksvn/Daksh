import Image from "next/image";
import Link from "next/link";
export function DakshaBrand({compact=false,href="/"}:{compact?:boolean;href?:string}){
 return <Link href={href} aria-label="Daksha — The Prajapati" className="inline-flex min-w-0 items-center gap-2.5">
  <Image src="/daksha-lotus.svg" alt="" width={compact?32:42} height={compact?32:42} priority/>
  {!compact&&<span className="flex min-w-0 flex-col leading-tight"><span className="text-lg font-extrabold tracking-tight">Daksha</span><span className="text-[10px] font-medium tracking-[0.18em] text-muted">THE PRAJAPATI</span></span>}
 </Link>;
}
