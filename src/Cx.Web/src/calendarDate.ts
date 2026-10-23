export type CalendarDate = { year: number; month: number; day: number };

export const calendarMonths = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

export function calendarDigits(value: string | number): string {
  return String(value);
}

export function isIsoDate(iso: string): boolean {
  return /^\d{4}-\d{2}-\d{2}$/.test(iso) && !Number.isNaN(Date.parse(iso)) && new Date(iso).toISOString().slice(0, 10) === iso;
}

export function toCalendarDate(iso: string): CalendarDate {
  const [year, month, day] = iso.split("-").map(Number);
  return { year, month, day };
}

/** Report boundaries use UTC consistently in the browser and API. */
export function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

export function formatCalendarDate(iso: string): string {
  return isIsoDate(iso) ? iso.replaceAll("-", "/") : "";
}

export function toGregorianDate({ year, month, day }: CalendarDate): string | null {
  if (![year, month, day].every(Number.isInteger) || year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > 31) return null;
  const iso = `${String(year).padStart(4, "0")}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}`;
  return isIsoDate(iso) ? iso : null;
}

export function parseCalendarDate(text: string): string | null {
  const value = text.trim();
  const match = /^(\d{4})[\/.-](\d{1,2})[\/.-](\d{1,2})$/.exec(value) || /^(\d{4})(\d{2})(\d{2})$/.exec(value);
  return match ? toGregorianDate({ year: Number(match[1]), month: Number(match[2]), day: Number(match[3]) }) : null;
}

export function calendarMonthDays(year: number, month: number): number {
  const date = new Date(0);
  date.setUTCFullYear(year, month, 0);
  return date.getUTCDate();
}
