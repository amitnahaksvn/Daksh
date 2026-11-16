"use client";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState, type FormEvent } from "react";
import { DakshaBrand } from "@/components/DakshaBrand";
import { AppearanceControls } from "@/components/ThemeToggle";
import { buttonClass, inputClass, Notice, tileClass } from "@/components/ui";
import { api, ApiError, type AccountProfile } from "@/lib/api";
import { setSession } from "@/lib/session";
function LoginForm(){
 const justVerified=useSearchParams().get("verified")==="1";
 const [email,setEmail]=useState(""); const [password,setPassword]=useState(""); const [error,setError]=useState<string|null>(null);
 const [profile,setProfile]=useState<AccountProfile|null>(null); const [submitting,setSubmitting]=useState(false);
 async function onSubmit(event:FormEvent){event.preventDefault();setError(null);setSubmitting(true);try{const result=await api.login(email,password);setSession(result);setProfile(result)}catch(err){setError(err instanceof ApiError?err.message:"Something went wrong. Please try again.")}finally{setSubmitting(false)}}
 if(profile)return <section className={`p-6 ${tileClass}`}><h1 className="mb-4 text-2xl font-extrabold">Signed in as {profile.firstName} {profile.lastName}</h1><p className="mb-4 text-sm text-muted">{profile.email}</p><Link href="/dashboard" className={`${buttonClass} w-full`}>Continue to dashboard</Link></section>;
 return <section className={`p-6 ${tileClass}`}><h1 className="mb-4 text-2xl font-extrabold">Sign in</h1>{justVerified&&<Notice>Account verified — please sign in.</Notice>}{error&&<p role="alert" className="mb-4 rounded-[1.25rem] bg-danger-bg p-4 text-sm text-danger">{error}</p>}
 <form onSubmit={onSubmit} className="space-y-3"><input required type="email" placeholder="Email" className={inputClass} value={email} onChange={e=>setEmail(e.target.value)}/><input required type="password" placeholder="Password" className={inputClass} value={password} onChange={e=>setPassword(e.target.value)}/><button type="submit" disabled={submitting} className={`${buttonClass} w-full`}>{submitting?"Signing in…":"Sign in"}</button></form>
 <p className="mt-4 text-center text-sm text-muted">New to Daksha? <Link href="/register" className="font-semibold text-text underline">Create an account</Link></p></section>;
}
export default function LoginPage(){return <main className="mx-auto w-full max-w-md p-3 py-10 md:p-4 md:py-16"><div className="mb-3 flex items-center justify-between px-2"><DakshaBrand/><AppearanceControls className="justify-end"/></div><Suspense fallback={null}><LoginForm/></Suspense></main>}
