import type { CSSProperties } from "react";
const paths = {
  layers: "m12 3 10 5-10 5L2 8l10-5z M2 12l10 5 10-5 M2 16l10 5 10-5",
  briefcase: "M8 6V3h8v3 M3 6h18v15H3V6z M3 11l9 4 9-4 M10 13h4",
  followup: "M14 21H3v-2a5 5 0 0 1 5-5h3 M9 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8 M18 13a5 5 0 1 0 0 10 5 5 0 0 0 0-10 M18 16v2l2 1",
  outcome: "M21 11v1a9 9 0 1 1-5-8 M9 11l3 3L22 4",
  campaign: "M3 10h5l12-6v16L8 14H3v-4z M8 14l2 7H6l-2-7 M22 10v4",
  journey: "M5 3a2 2 0 1 0 0 4 2 2 0 0 0 0-4 M19 17a2 2 0 1 0 0 4 2 2 0 0 0 0-4 M7 5h9a4 4 0 0 1 0 8H8a4 4 0 0 0 0 8h9",
  user: "M20 21v-2a6 6 0 0 0-6-6h-4a6 6 0 0 0-6 6v2 M12 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8",
  bell: "M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9 M10 21h4",
  overview: "M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z",
  inbox: "M4 4h16l2 12v4H2v-4L4 4z M2 15h6l2 3h4l2-3h6",
  voice:
    "M12 3a3 3 0 0 0-3 3v6a3 3 0 0 0 6 0V6a3 3 0 0 0-3-3z M5 10v2a7 7 0 0 0 14 0v-2 M12 19v3 M8 22h8",
  users:
    "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M9 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8 M17 4a4 4 0 0 1 0 8 M22 21v-2a4 4 0 0 0-3-3.87",
  spark:
    "m12 3 2.5 6.5L21 12l-6.5 2.5L12 21l-2.5-6.5L3 12l6.5-2.5L12 3z M20 2v4 M18 4h4",
  settings: "M4 7h16 M4 17h16 M8 4v6 M16 14v6",
  search: "M10.5 3a7.5 7.5 0 1 0 0 15 7.5 7.5 0 0 0 0-15 M16 16l5 5",
  plus: "M12 5v14 M5 12h14",
  arrow: "M19 12H5 M11 6l-6 6 6 6",
  check: "m5 12 4 4L19 6",
  close: "m6 6 12 12 M6 18 18 6",
  menu: "M4 6h16 M4 12h16 M4 18h16",
  chevron: "m6 9 6 6 6-6",
  refresh: "M20 7v5h-5 M4 17v-5h5 M6 6a8 8 0 0 1 14 6 M18 18a8 8 0 0 1-14-6",
  upload: "M12 16V3 M7 8l5-5 5 5 M4 16v5h16v-5",
  message: "M21 11a9 9 0 0 1-9 9H3l1.5-4.5A9 9 0 1 1 21 11z M8 10h8 M8 14h5",
  chart: "M4 3v18h17 M8 16l4-5 4 2 5-7",
  clock: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18 M12 7v5l3 2",
  calendar: "M5 5h14a2 2 0 0 1 2 2v13H3V7a2 2 0 0 1 2-2z M7 3v4 M17 3v4 M3 11h18 M7 15h2 M15 15h2",
  alert: "m12 3 10 18H2L12 3z M12 9v5 M12 17v1",
  stop: "M7 7h10v10H7z",
  edit: "M4 20h4L19 9l-4-4L4 16v4z M13 7l4 4",
  filter: "M4 5h16l-6 8v5l-4 2v-7L4 5z",
  eye: "M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6",
  "eye-off": "M3 3l18 18 M10.6 5.1A10 10 0 0 1 12 5c6.5 0 10 7 10 7a17 17 0 0 1-3 3.9 M6.6 6.6C3.9 8.3 2 12 2 12s3.5 7 10 7a9.7 9.7 0 0 0 5.4-1.6 M9.9 9.9a3 3 0 0 0 4.2 4.2",
  sun: "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8 M12 2v2 M12 20v2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M2 12h2 M20 12h2 M4.9 19.1l1.4-1.4 M17.7 6.3l1.4-1.4",
  moon: "M20 14.5A8.5 8.5 0 0 1 9.5 4 8.5 8.5 0 1 0 20 14.5z",
  monitor: "M3 4h18v12H3z M8 20h8 M12 16v4",
  logout: "M15 4h4v16h-4 M10 8l-4 4 4 4 M6 12h10",
  trash: "M4 7h16 M9 7V4h6v3 M6 7l1 13h10l1-13 M10 11v6 M14 11v6",
  tree: "M5 4h6v5H5z M13 15h6v5h-6z M13 4h6v5h-6z M8 9v8.5h5 M11 6.5h2",
} as const;
export type IconName = keyof typeof paths;
export default function Icon({
  name,
  size = 20,
  style,
}: {
  name: IconName;
  size?: number;
  style?: CSSProperties;
}) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      style={style}
    >
      <path d={paths[name]} />
    </svg>
  );
}
