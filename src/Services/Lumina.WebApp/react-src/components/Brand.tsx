import { Boxes } from "lucide-react";
import { usePreferences } from "../i18n/PreferencesContext";

export function Brand({ compact = false }: { compact?: boolean }) {
  const { t } = usePreferences();
  return (
    <div className={`brand ${compact ? "brand--compact" : ""}`}>
      <span className="brand__mark"><Boxes aria-hidden="true" /></span>
      {!compact && <span className="brand__copy"><strong>Lumina</strong><small>{t("brand.tagline")}</small></span>}
    </div>
  );
}
