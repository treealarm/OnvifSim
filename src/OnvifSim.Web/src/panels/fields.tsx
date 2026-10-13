import { useId, useState, type ReactNode } from 'react';

interface NumberFieldProps {
  label: string;
  value: number;
  onChange: (value: number) => void;
  min?: number;
  max?: number;
  step?: number;
  // Adds a slider next to the box, for values worth sweeping through (angles, image settings).
  slider?: boolean;
}

// A number box that lets the user type freely: a value goes out only once it is a number within the
// limits, and leaving the box shows the value in effect again.
export function NumberField({ label, value, onChange, min = -Infinity, max = Infinity, step = 1, slider = false }: NumberFieldProps) {
  const id = useId();
  const [draft, setDraft] = useState<string | null>(null);
  // Where the slider is held: the value it sets may take a while to come back (a PTZ head turning).
  const [sliding, setSliding] = useState<number | null>(null);
  const shown = Number(value.toFixed(2));

  function slide(number: number) {
    setSliding(number);
    onChange(number);
  }

  function type(text: string) {
    setDraft(text);
    const number = Number(text);
    if (text.trim() !== '' && Number.isFinite(number) && number >= min && number <= max) onChange(number);
  }

  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {slider && (
        <input
          type="range"
          aria-label={`${label} slider`}
          min={min}
          max={max}
          step={step}
          value={sliding ?? shown}
          onChange={(e) => slide(Number(e.target.value))}
          onPointerUp={() => setSliding(null)}
          onKeyUp={() => setSliding(null)}
          onBlur={() => setSliding(null)}
        />
      )}
      <input
        id={id}
        type="number"
        className={draft !== null && Number(draft) !== shown ? 'pending' : undefined}
        min={Number.isFinite(min) ? min : undefined}
        max={Number.isFinite(max) ? max : undefined}
        step={step}
        value={draft ?? shown}
        onChange={(e) => type(e.target.value)}
        onBlur={() => setDraft(null)}
      />
    </div>
  );
}

export function TextField({ label, value, onChange, type = 'text' }: { label: string; value: string; onChange: (value: string) => void; type?: 'text' | 'password' }) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input id={id} type={type} value={value} onChange={(e) => onChange(e.target.value)} />
    </div>
  );
}

export function CheckboxField({ label, checked, onChange }: { label: string; checked: boolean; onChange: (checked: boolean) => void }) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input id={id} type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
    </div>
  );
}

interface SelectFieldProps<T extends string> {
  label: string;
  value: T;
  options: readonly { value: T; label: string }[];
  onChange: (value: T) => void;
}

export function SelectField<T extends string>({ label, value, options, onChange }: SelectFieldProps<T>) {
  const id = useId();
  function choose(chosen: string) {
    const option = options.find((o) => o.value === chosen);
    if (option) onChange(option.value);
  }
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <select id={id} value={value} onChange={(e) => choose(e.target.value)}>
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    </div>
  );
}

export function ColorField({ label, value, onChange }: { label: string; value: string; onChange: (value: string) => void }) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input id={id} type="color" value={value} onChange={(e) => onChange(e.target.value)} />
    </div>
  );
}

export function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="section">
      <h3>{title}</h3>
      {children}
    </section>
  );
}
