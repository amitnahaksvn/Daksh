"use client";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";
import { AppearanceControls } from "@/components/ThemeToggle";
import { Orb } from "@/components/Orb";
import { buttonClass, errorInputClass, inputClass, Notice, Select, tileClass } from "@/components/ui";
import { api, ApiError } from "@/lib/api";

const GENDER_OPTIONS = ["Female", "Male", "Non-binary", "Prefer not to say"];
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

const FIELDS = ["firstName", "lastName", "gender", "dateOfBirth", "email", "password", "confirmPassword"] as const;
type Field = (typeof FIELDS)[number];
type Values = Record<Field, string>;
type FieldErrors = Partial<Record<Field, string>>;

const EMPTY_VALUES: Values = {
  firstName: "",
  lastName: "",
  gender: "",
  dateOfBirth: "",
  email: "",
  password: "",
  confirmPassword: "",
};

/** Validates one field against the full form (password/confirmPassword each depend on the other). */
function validateField(field: Field, v: Values): string | undefined {
  switch (field) {
    case "firstName":
      return v.firstName.trim() ? undefined : "First name is required.";
    case "lastName":
      return v.lastName.trim() ? undefined : "Last name is required.";
    case "gender":
      return v.gender ? undefined : "Please select a gender.";
    case "dateOfBirth":
      return v.dateOfBirth ? undefined : "Date of birth is required.";
    case "email":
      if (!v.email.trim()) return "Email is required.";
      if (!EMAIL_RE.test(v.email)) return "Enter a valid email address.";
      return undefined;
    case "password":
      if (!v.password) return "Password is required.";
      if (v.password.length < 10) return "Password must be at least 10 characters.";
      if (!/[A-Za-z]/.test(v.password) || !/[0-9]/.test(v.password)) return "Password needs at least one letter and one number.";
      return undefined;
    case "confirmPassword":
      if (!v.confirmPassword) return "Please confirm your password.";
      if (v.confirmPassword !== v.password) return "Passwords do not match.";
      return undefined;
  }
}

function validateAll(v: Values): FieldErrors {
  const errors: FieldErrors = {};
  for (const field of FIELDS) {
    const message = validateField(field, v);
    if (message) errors[field] = message;
  }
  return errors;
}

function FieldError({ message }: { message?: string }) {
  if (!message) return null;
  return (
    <p role="alert" className="mt-1 text-xs font-medium text-danger">
      {message}
    </p>
  );
}

export default function RegisterPage() {
  const router = useRouter();
  const [values, setValues] = useState<Values>(EMPTY_VALUES);
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  /** Validates live as the user types, so a field's red state updates the moment the data becomes valid (or invalid). */
  function handleChange(field: Field, value: string) {
    const next = { ...values, [field]: value };
    setValues(next);
    setFieldErrors((prev) => {
      const updated = { ...prev, [field]: validateField(field, next) };
      // Keep confirmPassword's "match" state in sync when password itself changes.
      if (field === "password" && next.confirmPassword) {
        updated.confirmPassword = validateField("confirmPassword", next);
      }
      return updated;
    });
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    const errors = validateAll(values);
    setFieldErrors(errors);
    if (Object.keys(errors).length > 0) return;

    setSubmitting(true);
    try {
      const { firstName, lastName, gender, dateOfBirth, email, password } = values;
      await api.register({ firstName, lastName, gender, dateOfBirth, email, password });
      router.push(`/verify-otp?email=${encodeURIComponent(values.email)}`);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="mx-auto w-full max-w-md p-3 py-10 md:p-4 md:py-16">
      <div className="mb-3 flex items-center justify-between px-2">
        <Link href="/" className="flex items-center gap-2.5 text-xl font-extrabold tracking-tight"><Orb size={26} />Daksh</Link>
        <AppearanceControls className="justify-end" />
      </div>
      <section className={`p-6 ${tileClass}`}>
        <h1 className="mb-4 text-2xl font-extrabold">Create account</h1>
        {error && <Notice>{error}</Notice>}
        <form onSubmit={onSubmit} noValidate className="space-y-3">
          <div className="grid grid-cols-2 gap-3">
            <div>
              <input
                placeholder="First name"
                className={`${inputClass} ${fieldErrors.firstName ? errorInputClass : ""}`}
                value={values.firstName}
                onChange={(e) => handleChange("firstName", e.target.value)}
              />
              <FieldError message={fieldErrors.firstName} />
            </div>
            <div>
              <input
                placeholder="Last name"
                className={`${inputClass} ${fieldErrors.lastName ? errorInputClass : ""}`}
                value={values.lastName}
                onChange={(e) => handleChange("lastName", e.target.value)}
              />
              <FieldError message={fieldErrors.lastName} />
            </div>
          </div>

          <div>
            <Select invalid={!!fieldErrors.gender} value={values.gender} onChange={(e) => handleChange("gender", e.target.value)}>
              <option value="" disabled>
                Select gender
              </option>
              {GENDER_OPTIONS.map((g) => (
                <option key={g} value={g}>
                  {g}
                </option>
              ))}
            </Select>
            <FieldError message={fieldErrors.gender} />
          </div>

          <div>
            <label className="block text-sm text-muted">
              Date of birth
              <input
                type="date"
                className={`${inputClass} mt-1 ${fieldErrors.dateOfBirth ? errorInputClass : ""}`}
                value={values.dateOfBirth}
                onChange={(e) => handleChange("dateOfBirth", e.target.value)}
              />
            </label>
            <FieldError message={fieldErrors.dateOfBirth} />
          </div>

          <div>
            <input
              type="email"
              placeholder="Email"
              className={`${inputClass} ${fieldErrors.email ? errorInputClass : ""}`}
              value={values.email}
              onChange={(e) => handleChange("email", e.target.value)}
            />
            <FieldError message={fieldErrors.email} />
          </div>

          <div>
            <input
              type="password"
              placeholder="Password (min 10 characters, letters and numbers)"
              className={`${inputClass} ${fieldErrors.password ? errorInputClass : ""}`}
              value={values.password}
              onChange={(e) => handleChange("password", e.target.value)}
            />
            <FieldError message={fieldErrors.password} />
          </div>

          <div>
            <input
              type="password"
              placeholder="Confirm password"
              className={`${inputClass} ${fieldErrors.confirmPassword ? errorInputClass : ""}`}
              value={values.confirmPassword}
              onChange={(e) => handleChange("confirmPassword", e.target.value)}
            />
            <FieldError message={fieldErrors.confirmPassword} />
          </div>

          <button type="submit" disabled={submitting} className={`${buttonClass} w-full`}>
            {submitting ? "Sending code…" : "Create account"}
          </button>
        </form>
        <p className="mt-4 text-center text-sm text-muted">
          Already have an account? <Link href="/login" className="font-semibold text-text underline">Sign in</Link>
        </p>
      </section>
    </main>
  );
}
