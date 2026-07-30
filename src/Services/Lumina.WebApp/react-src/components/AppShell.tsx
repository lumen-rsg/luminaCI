import {
  Activity, Boxes, ChevronRight, Clock3, Database, GitBranch, Hammer, LogOut,
  Menu, Moon, PackageSearch, PanelLeftClose, PanelLeftOpen, Settings, ShieldCheck, Sun, X
} from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { useAuth } from "../auth/AuthContext";
import { usePreferences } from "../i18n/PreferencesContext";
import type { TranslationKey } from "../i18n/translations";
import { NavLink, useLocation } from "../lib/router";
import { Brand } from "./Brand";
import { SettingsPanel } from "./SettingsPanel";

const navigation = [
  { to: "/", label: "nav.overview" as TranslationKey, icon: Activity, end: true },
  { to: "/builds", label: "nav.builds" as TranslationKey, icon: Hammer },
  { to: "/pipelines", label: "nav.pipelines" as TranslationKey, icon: GitBranch },
  { to: "/sources", label: "nav.sources" as TranslationKey, icon: PackageSearch },
  { to: "/repositories", label: "nav.repositories" as TranslationKey, icon: Database },
  { to: "/security", label: "nav.security" as TranslationKey, icon: ShieldCheck }
];

export function AppShell({ children }: { children: ReactNode }) {
  const { user, logout } = useAuth();
  const { t, locale, effectiveTheme, setTheme } = usePreferences();
  const location = useLocation();
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem("lumina:sidebar") === "collapsed");
  const [mobileOpen, setMobileOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [now, setNow] = useState(new Date());

  useEffect(() => { setMobileOpen(false); }, [location.pathname]);
  useEffect(() => {
    const timer = window.setInterval(() => setNow(new Date()), 30_000);
    return () => window.clearInterval(timer);
  }, []);

  const toggleCollapsed = () => {
    setCollapsed(value => {
      localStorage.setItem("lumina:sidebar", value ? "expanded" : "collapsed");
      return !value;
    });
  };
  const path = location.pathname.split("/").filter(Boolean);
  const breadcrumbLabel = (segment: string) => {
    const item = navigation.find(candidate => candidate.to === `/${segment}`);
    return item ? t(item.label) : segment;
  };

  return (
    <div className={`shell ${collapsed ? "shell--collapsed" : ""}`}>
      <aside className={`sidebar ${mobileOpen ? "sidebar--open" : ""}`}>
        <div className="sidebar__brand"><Brand compact={collapsed} /><button className="mobile-close" onClick={() => setMobileOpen(false)} aria-label={t("shell.closeNav")}><X /></button></div>
        <nav aria-label={t("nav.primary")}>
          <span className="nav-label">{collapsed ? "—" : t("nav.workspace")}</span>
          {navigation.map(item => <NavLink key={item.to} to={item.to} end={item.end} title={collapsed ? t(item.label) : undefined}><item.icon /><span>{t(item.label)}</span></NavLink>)}
        </nav>
        <div className="sidebar__footer">
          <div className="identity">
            <span className="identity__avatar">{user?.username?.slice(0, 2).toUpperCase()}</span>
            <span><strong>{user?.username}</strong><small>{user?.role || t("shell.operator")}</small></span>
          </div>
          <button className="icon-button" onClick={() => void logout()} title={t("shell.signOut")} aria-label={t("shell.signOut")}><LogOut /></button>
        </div>
      </aside>
      {mobileOpen && <button className="sidebar-scrim" onClick={() => setMobileOpen(false)} aria-label={t("shell.closeNav")} />}
      <div className="workspace">
        <header className="topbar">
          <div className="topbar__left">
            <button className="icon-button mobile-menu" onClick={() => setMobileOpen(true)} aria-label={t("shell.openNav")}><Menu /></button>
            <button className="icon-button collapse-button" onClick={toggleCollapsed} aria-label={collapsed ? t("shell.expandNav") : t("shell.collapseNav")}>{collapsed ? <PanelLeftOpen /> : <PanelLeftClose />}</button>
            <div className="breadcrumbs"><span>{t("shell.controlPlane")}</span><a href="/">console</a>{path.map(segment => <span key={segment}><ChevronRight />{breadcrumbLabel(segment)}</span>)}</div>
          </div>
          <div className="topbar__actions">
            <span className="online"><i />{t("shell.systemsOnline")}</span>
            <span className="clock"><Clock3 /><span><small>{t("shell.localTime")}</small>{now.toLocaleTimeString(locale, { hour: "2-digit", minute: "2-digit" })}</span></span>
            <button className="icon-button" onClick={() => setSettingsOpen(true)} aria-label={t("shell.settings")} title={t("shell.settings")}><Settings /></button>
            <button className="icon-button" onClick={() => setTheme(effectiveTheme === "dark" ? "light" : "dark")} aria-label={effectiveTheme === "dark" ? t("shell.lightTheme") : t("shell.darkTheme")}>{effectiveTheme === "dark" ? <Sun /> : <Moon />}</button>
          </div>
        </header>
        <main id="main-content">{children}</main>
      </div>
      <SettingsPanel open={settingsOpen} onClose={() => setSettingsOpen(false)} />
    </div>
  );
}

export function AppLoading() {
  const { t } = usePreferences();
  return <main className="boot"><Boxes /><span>{t("shell.secureSession")}</span></main>;
}
