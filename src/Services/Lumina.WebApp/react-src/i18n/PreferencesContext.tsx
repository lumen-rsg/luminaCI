import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { en, ru, type TranslationKey } from "./translations";

export type Locale = "en" | "ru";
export type ThemePreference = "system" | "dark" | "light";
export type Density = "comfortable" | "compact";

type Preferences = {
  locale: Locale;
  theme: ThemePreference;
  density: Density;
  refreshSeconds: number;
};

type PreferencesContextValue = Preferences & {
  effectiveTheme: "dark" | "light";
  setLocale: (value: Locale) => void;
  setTheme: (value: ThemePreference) => void;
  setDensity: (value: Density) => void;
  setRefreshSeconds: (value: number) => void;
  t: (key: TranslationKey, values?: Record<string, string | number>) => string;
};

const PreferencesContext = createContext<PreferencesContextValue | null>(null);
const storageKey = "lumina:preferences";

function initialPreferences(): Preferences {
  const fallback: Preferences = {
    locale: navigator.language.toLowerCase().startsWith("ru") ? "ru" : "en",
    theme: "system",
    density: "comfortable",
    refreshSeconds: 15
  };
  try {
    const saved = JSON.parse(localStorage.getItem(storageKey) ?? "{}") as Partial<Preferences>;
    return {
      locale: saved.locale === "en" || saved.locale === "ru" ? saved.locale : fallback.locale,
      theme: saved.theme === "system" || saved.theme === "dark" || saved.theme === "light" ? saved.theme : fallback.theme,
      density: saved.density === "comfortable" || saved.density === "compact" ? saved.density : fallback.density,
      refreshSeconds: [0, 5, 15, 30].includes(saved.refreshSeconds ?? -1) ? saved.refreshSeconds! : fallback.refreshSeconds
    };
  } catch {
    return fallback;
  }
}

export function PreferencesProvider({ children }: { children: ReactNode }) {
  const [preferences, setPreferences] = useState(initialPreferences);
  const [systemDark, setSystemDark] = useState(() => matchMedia("(prefers-color-scheme: dark)").matches);
  const effectiveTheme = preferences.theme === "system" ? (systemDark ? "dark" : "light") : preferences.theme;

  useEffect(() => {
    const query = matchMedia("(prefers-color-scheme: dark)");
    const update = () => setSystemDark(query.matches);
    query.addEventListener("change", update);
    return () => query.removeEventListener("change", update);
  }, []);

  useEffect(() => {
    localStorage.setItem(storageKey, JSON.stringify(preferences));
    document.documentElement.dataset.theme = effectiveTheme;
    document.documentElement.dataset.density = preferences.density;
    document.documentElement.lang = preferences.locale;
  }, [effectiveTheme, preferences]);

  const value = useMemo<PreferencesContextValue>(() => {
    const dictionary = preferences.locale === "ru" ? ru : en;
    const patch = (change: Partial<Preferences>) => setPreferences(current => ({ ...current, ...change }));
    return {
      ...preferences,
      effectiveTheme,
      setLocale: locale => patch({ locale }),
      setTheme: theme => patch({ theme }),
      setDensity: density => patch({ density }),
      setRefreshSeconds: refreshSeconds => patch({ refreshSeconds }),
      t: (key, values) => Object.entries(values ?? {}).reduce(
        (text, [name, replacement]) => text.replaceAll(`{${name}}`, String(replacement)),
        dictionary[key]
      )
    };
  }, [effectiveTheme, preferences]);

  return <PreferencesContext.Provider value={value}>{children}</PreferencesContext.Provider>;
}

export function usePreferences() {
  const context = useContext(PreferencesContext);
  if (!context) throw new Error("usePreferences must be used inside PreferencesProvider");
  return context;
}
