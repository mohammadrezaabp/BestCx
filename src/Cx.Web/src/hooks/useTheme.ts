import { useCallback, useEffect, useState } from "react";

export type ThemeChoice = "system" | "light" | "dark";
const KEY = "cx-theme";

function read(): ThemeChoice {
  try {
    const value = localStorage.getItem(KEY);
    return value === "light" || value === "dark" ? value : "system";
  } catch {
    return "system";
  }
}

/** Applies the choice to <html data-theme>; "system" follows the OS setting via CSS. */
function apply(choice: ThemeChoice) {
  const root = document.documentElement;
  if (choice === "system") delete root.dataset.theme;
  else root.dataset.theme = choice;
}

export function useTheme() {
  const [choice, setChoice] = useState<ThemeChoice>(read);

  useEffect(() => {
    apply(choice);
    try {
      if (choice === "system") localStorage.removeItem(KEY);
      else localStorage.setItem(KEY, choice);
    } catch {
      /* private mode: the choice lasts for this page only */
    }
  }, [choice]);

  /** system → light → dark → system */
  const cycle = useCallback(
    () => setChoice((c) => (c === "system" ? "light" : c === "light" ? "dark" : "system")),
    [],
  );

  return { choice, setChoice, cycle };
}
