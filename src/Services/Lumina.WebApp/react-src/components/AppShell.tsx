import {
  Activity, Boxes, ChevronRight, Clock3, Database, GitBranch, Hammer, LogOut,
  Menu, Moon, PackageSearch, PanelLeftClose, PanelLeftOpen, ShieldCheck, Sun, X
} from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { useAuth } from "../auth/AuthContext";
import { NavLink, useLocation } from "../lib/router";
import { Brand } from "./Brand";

const navigation = [
  { to: "/", label: "Overview", icon: Activity, end: true },
  { to: "/builds", label: "Builds", icon: Hammer },
  { to: "/pipelines", label: "Pipelines", icon: GitBranch },
  { to: "/sources", label: "Sources", icon: PackageSearch },
  { to: "/repositories", label: "Repositories", icon: Database },
  { to: "/security", label: "Security", icon: ShieldCheck }
];

export function AppShell({ children }: { children: ReactNode }) {
  const { user, logout } = useAuth();
  const location = useLocation();
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem("lumina:sidebar") === "collapsed");
  const [mobileOpen, setMobileOpen] = useState(false);
  const [dark, setDark] = useState(() => localStorage.getItem("lumina:theme") !== "light");
  const [now, setNow] = useState(new Date());

  useEffect(() => { setMobileOpen(false); }, [location.pathname]);
  useEffect(() => {
    document.documentElement.dataset.theme = dark ? "dark" : "light";
    localStorage.setItem("lumina:theme", dark ? "dark" : "light");
  }, [dark]);
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

  return (
    <div className={`shell ${collapsed ? "shell--collapsed" : ""}`}>
      <aside className={`sidebar ${mobileOpen ? "sidebar--open" : ""}`}>
        <div className="sidebar__brand"><Brand compact={collapsed} /><button className="mobile-close" onClick={() => setMobileOpen(false)} aria-label="Close navigation"><X /></button></div>
        <nav aria-label="Primary navigation">
          <span className="nav-label">{collapsed ? "—" : "Workspace"}</span>
          {navigation.map(item => <NavLink key={item.to} to={item.to} end={item.end} title={collapsed ? item.label : undefined}><item.icon /><span>{item.label}</span></NavLink>)}
        </nav>
        <div className="sidebar__footer">
          <div className="identity">
            <span className="identity__avatar">{user?.username?.slice(0, 2).toUpperCase()}</span>
            <span><strong>{user?.username}</strong><small>{user?.role || "Operator"}</small></span>
          </div>
          <button className="icon-button" onClick={() => void logout()} title="Sign out" aria-label="Sign out"><LogOut /></button>
        </div>
      </aside>
      {mobileOpen && <button className="sidebar-scrim" onClick={() => setMobileOpen(false)} aria-label="Close navigation" />}
      <div className="workspace">
        <header className="topbar">
          <div className="topbar__left">
            <button className="icon-button mobile-menu" onClick={() => setMobileOpen(true)} aria-label="Open navigation"><Menu /></button>
            <button className="icon-button collapse-button" onClick={toggleCollapsed} aria-label={collapsed ? "Expand navigation" : "Collapse navigation"}>{collapsed ? <PanelLeftOpen /> : <PanelLeftClose />}</button>
            <div className="breadcrumbs"><span>Control plane</span><a href="/">console</a>{path.map(segment => <span key={segment}><ChevronRight />{segment}</span>)}</div>
          </div>
          <div className="topbar__actions">
            <span className="online"><i />Systems online</span>
            <span className="clock"><Clock3 /><span><small>Local time</small>{now.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span></span>
            <button className="icon-button" onClick={() => setDark(value => !value)} aria-label={`Switch to ${dark ? "light" : "dark"} theme`}>{dark ? <Sun /> : <Moon />}</button>
          </div>
        </header>
        <main id="main-content">{children}</main>
      </div>
    </div>
  );
}

export function AppLoading() {
  return <main className="boot"><Boxes /><span>Resolving secure session…</span></main>;
}
