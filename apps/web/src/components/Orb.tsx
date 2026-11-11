/**
 * Daksh's presence: a small living orb. It turns slowly, and pulses faster while Daksh is thinking. It is decoration only
 * (hidden from screen readers); anything it signals is also said in words next to it.
 */
export function Orb({ size = 32, thinking = false, className = "" }: { size?: number; thinking?: boolean; className?: string }) {
  return <span aria-hidden="true" className={`orb ${thinking ? "orb-thinking" : ""} ${className}`} style={{ width: size, height: size }} />;
}
