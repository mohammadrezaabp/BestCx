import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { formatCalendarDate, isIsoDate, parseCalendarDate, calendarDigits, calendarMonthDays, calendarMonths, todayIso, toGregorianDate, toCalendarDate } from "../calendarDate";
import Icon from "./Icon";

type Props = {
  label: string;
  value: string;
  onChange: (value: string) => void;
  required?: boolean;
  min?: string;
  max?: string;
};
const weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

export default function DatePicker({ label, value, onChange, required, min, max }: Props) {
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const field = useRef<HTMLDivElement>(null);
  const popup = useRef<HTMLDivElement>(null);
  const focusCalendar = useRef(true);
  const [text, setText] = useState(() => formatCalendarDate(value));
  const [error, setError] = useState("");
  const [open, setOpen] = useState(false);
  const [month, setMonth] = useState(() => toCalendarDate(isIsoDate(value) ? value : todayIso()));
  const [position, setPosition] = useState({ top: 0, left: 0, width: 304 });
  const today = todayIso();
  const allowed = (iso: string) => (!min || iso >= min) && (!max || iso <= max);

  useEffect(() => {
    setText(formatCalendarDate(value));
    setError(current => required && !value && current ? "Select a date." : "");
    input.current?.setCustomValidity(required && !value ? "Select a date." : "");
  }, [value, required]);

  useLayoutEffect(() => {
    if (!open) return;
    const place = () => {
      const rect = field.current!.getBoundingClientRect();
      const width = Math.min(304, window.innerWidth - 16);
      const height = popup.current!.offsetHeight;
      const below = rect.bottom + 6;
      setPosition({ width, left: Math.max(8, Math.min(rect.left, window.innerWidth - width - 8)),
        top: below + height <= window.innerHeight - 8 ? below : Math.max(8, rect.top - height - 6) });
    };
    const outside = (event: PointerEvent) => {
      if (!field.current?.contains(event.target as Node) && !popup.current?.contains(event.target as Node)) setOpen(false);
    };
    place();
    if (focusCalendar.current) popup.current?.focus();
    const resize = new ResizeObserver(place);
    resize.observe(popup.current!);
    document.addEventListener("pointerdown", outside);
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      resize.disconnect();
      document.removeEventListener("pointerdown", outside);
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [open]);

  const close = () => { setOpen(false); input.current?.focus(); };
  const choose = (iso: string) => {
    setText(formatCalendarDate(iso));
    setError("");
    input.current?.setCustomValidity("");
    onChange(iso);
    close();
  };
  const showCalendar = (moveFocus = true) => {
    focusCalendar.current = moveFocus;
    if (!open) {
      setMonth(toCalendarDate(parseCalendarDate(text) || (isIsoDate(value) ? value : today)));
      setOpen(true);
    } else if (moveFocus) popup.current?.focus();
  };
  const edit = (next: string) => {
    setOpen(false);
    setText(next);
    setError("");
    const iso = parseCalendarDate(next);
    const message = next && !iso ? "Enter a valid date, for example 2026/10/23" : !next && required ? "Select a date." : "";
    input.current?.setCustomValidity(message);
  };
  const commit = () => {
    const iso = parseCalendarDate(text);
    if (iso || !text) {
      setText(formatCalendarDate(iso || ""));
      onChange(iso || "");
    }
    if (!required) setError(input.current?.validationMessage || "");
  };
  const move = (delta: number) => {
    const index = month.year * 12 + month.month - 1 + delta;
    setMonth({ year: Math.floor(index / 12), month: index % 12 + 1, day: 1 });
  };
  const first = toGregorianDate({ ...month, day: 1 })!;
  const offset = new Date(`${first}T12:00:00Z`).getUTCDay();
  const count = calendarMonthDays(month.year, month.month);
  const slots = Math.ceil((offset + count) / 7) * 7;
  const yearStart = Math.max(1, month.year - 100);
  const yearEnd = Math.min(9999, month.year + 100);

  return (
    <div className="grid-filter calendar-date-filter">
      <label htmlFor={id}>{label}</label>
      <div className={`calendar-date-field${open ? " is-open" : ""}`} ref={field}>
        <input ref={input} id={id} type="text" inputMode="numeric" dir="ltr" required={required}
          autoComplete="off" placeholder="Select date" value={text}
          aria-invalid={error ? true : undefined} aria-describedby={error ? `${id}-error` : undefined}
          aria-expanded={open} aria-controls={open ? `${id}-calendar` : undefined}
          aria-haspopup="dialog"
          onClick={() => showCalendar(false)}
          onChange={event => edit(event.target.value)}
          onBlur={commit}
          onInvalid={() => setError(input.current?.validationMessage || "")}
          onKeyDown={event => {
            if (event.key === "Enter") commit();
            if (event.key === "Escape" && open) { event.preventDefault(); event.stopPropagation(); close(); }
            if (event.key === "ArrowDown") { event.preventDefault(); showCalendar(); }
          }} />
        <button type="button" className="icon-button" aria-label={`Open the calendar ${label}`}
          aria-haspopup="dialog" aria-expanded={open} aria-controls={open ? `${id}-calendar` : undefined}
          onPointerDown={event => event.preventDefault()}
          onClick={() => open ? close() : showCalendar()}>
          <Icon name="calendar" size={18} />
        </button>
      </div>
      {error && <span id={`${id}-error`} className="calendar-date-error" role="alert">{error}</span>}
      {open && createPortal(
        <div ref={popup} id={`${id}-calendar`} className="calendar-popup" role="dialog" tabIndex={-1} aria-label={`Calendar ${label}`} dir="ltr"
          style={position} onKeyDown={event => { if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); close(); } }}
          onBlur={event => { if (event.relatedTarget && !popup.current?.contains(event.relatedTarget as Node) && !field.current?.contains(event.relatedTarget as Node)) setOpen(false); }}>
          <div className="calendar-popup-heading">
            <button type="button" className="icon-button" aria-label="Previous month" disabled={month.year === 1 && month.month === 1} onClick={() => move(-1)}>‹</button>
            <select aria-label={`Month ${label}`} value={month.month} onChange={event => setMonth({ ...month, month: Number(event.target.value) })}>
              {calendarMonths.map((name, index) => <option key={name} value={index + 1}>{name}</option>)}
            </select>
            <select aria-label={`Year ${label}`} value={month.year} onChange={event => setMonth({ ...month, year: Number(event.target.value) })}>
              {Array.from({ length: yearEnd - yearStart + 1 }, (_, index) => yearStart + index).map(year => <option key={year} value={year}>{calendarDigits(year)}</option>)}
            </select>
            <button type="button" className="icon-button" aria-label="Next month" disabled={month.year === 9999 && month.month === 12} onClick={() => move(1)}>›</button>
          </div>
          <div className="calendar-popup-grid">
            {weekdays.map(day => <span className="calendar-popup-weekday" key={day} title={day}>{day.slice(0, 2)}</span>)}
            {Array.from({ length: slots }, (_, index) => {
              const day = index - offset + 1;
              if (day < 1 || day > count) return <span key={index} />;
              const iso = toGregorianDate({ ...month, day })!;
              return <button key={index} type="button" disabled={!allowed(iso)} aria-label={`${calendarDigits(day)} ${calendarMonths[month.month - 1]} ${calendarDigits(month.year)}`}
                aria-pressed={iso === value} aria-current={iso === today ? "date" : undefined}
                onClick={() => choose(iso)}>{calendarDigits(day)}</button>;
            })}
          </div>
          <div className="calendar-popup-actions">
            <button type="button" className="btn ghost small" disabled={!allowed(today)} onClick={() => choose(today)}>Today</button>
            {!required && <button type="button" className="btn ghost small" onClick={() => choose("")}>Clear</button>}
            <button type="button" className="btn ghost small" onClick={close}>Close</button>
          </div>
        </div>, field.current?.closest("dialog") ?? document.body)}
    </div>
  );
}
