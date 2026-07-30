import { Check, Monitor, Moon, Settings2, Sun, X } from "lucide-react";
import {
  usePreferences, type Density, type Locale, type ThemePreference
} from "../i18n/PreferencesContext";

type Choice<T extends string | number> = { value: T; label: string; icon?: typeof Sun };

function ChoiceGroup<T extends string | number>({ label, choices, value, onChange }: {
  label: string;
  choices: Choice<T>[];
  value: T;
  onChange: (value: T) => void;
}) {
  return (
    <div className="setting-choices" role="radiogroup" aria-label={label}>
      {choices.map(choice => {
        const Icon = choice.icon;
        return (
          <button key={choice.value} type="button" role="radio" aria-checked={value === choice.value}
            className={value === choice.value ? "selected" : ""} onClick={() => onChange(choice.value)}>
            {Icon && <Icon aria-hidden="true" />}<span>{choice.label}</span>{value === choice.value && <Check aria-hidden="true" />}
          </button>
        );
      })}
    </div>
  );
}

export function SettingsPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const {
    t, locale, setLocale, theme, setTheme, density, setDensity, refreshSeconds, setRefreshSeconds
  } = usePreferences();
  if (!open) return null;

  return (
    <div className="settings-backdrop" role="presentation" onMouseDown={event => event.target === event.currentTarget && onClose()}>
      <aside className="settings-panel" role="dialog" aria-modal="true" aria-labelledby="settings-title">
        <header>
          <span className="settings-panel__icon"><Settings2 /></span>
          <div><h2 id="settings-title">{t("settings.title")}</h2><p>{t("settings.subtitle")}</p></div>
          <button className="icon-button" onClick={onClose} aria-label={t("common.close")}><X /></button>
        </header>
        <div className="settings-panel__body">
          <section>
            <div><h3>{t("settings.language")}</h3><p>{t("settings.languageHint")}</p></div>
            <ChoiceGroup<Locale> label={t("settings.language")} value={locale} onChange={setLocale} choices={[
              { value: "en", label: t("settings.english") }, { value: "ru", label: t("settings.russian") }
            ]} />
          </section>
          <section>
            <div><h3>{t("settings.appearance")}</h3><p>{t("settings.appearanceHint")}</p></div>
            <ChoiceGroup<ThemePreference> label={t("settings.appearance")} value={theme} onChange={setTheme} choices={[
              { value: "system", label: t("settings.system"), icon: Monitor },
              { value: "dark", label: t("settings.dark"), icon: Moon },
              { value: "light", label: t("settings.light"), icon: Sun }
            ]} />
          </section>
          <section>
            <div><h3>{t("settings.density")}</h3><p>{t("settings.densityHint")}</p></div>
            <ChoiceGroup<Density> label={t("settings.density")} value={density} onChange={setDensity} choices={[
              { value: "comfortable", label: t("settings.comfortable") },
              { value: "compact", label: t("settings.compact") }
            ]} />
          </section>
          <section>
            <div><h3>{t("settings.refresh")}</h3><p>{t("settings.refreshHint")}</p></div>
            <ChoiceGroup<number> label={t("settings.refresh")} value={refreshSeconds} onChange={setRefreshSeconds} choices={[
              { value: 0, label: t("settings.off") }, { value: 5, label: t("settings.seconds", { count: 5 }) },
              { value: 15, label: t("settings.seconds", { count: 15 }) }, { value: 30, label: t("settings.seconds", { count: 30 }) }
            ]} />
          </section>
        </div>
        <footer>{t("settings.persisted")}</footer>
      </aside>
    </div>
  );
}
