import { useEffect } from "react";
import { usePreferences } from "../i18n/PreferencesContext";

export function useAutoRefresh(reload: () => void, active = true) {
  const { refreshSeconds } = usePreferences();

  useEffect(() => {
    if (!active || refreshSeconds <= 0) return;
    const timer = window.setInterval(reload, refreshSeconds * 1000);
    return () => window.clearInterval(timer);
  }, [active, refreshSeconds, reload]);
}
