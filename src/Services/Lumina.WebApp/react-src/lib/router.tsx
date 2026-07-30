import {
  createContext, useCallback, useContext, useEffect, useMemo, useState,
  type AnchorHTMLAttributes, type MouseEvent, type ReactNode
} from "react";

type LocationState = { pathname: string; state: unknown };
type RouterValue = LocationState & { navigate: (to: string, options?: { replace?: boolean; state?: unknown }) => void };

const RouterContext = createContext<RouterValue | null>(null);

export function RouterProvider({ children }: { children: ReactNode }) {
  const [location, setLocation] = useState<LocationState>(() => ({
    pathname: window.location.pathname,
    state: window.history.state
  }));

  useEffect(() => {
    const onPopState = () => setLocation({ pathname: window.location.pathname, state: window.history.state });
    window.addEventListener("popstate", onPopState);
    return () => window.removeEventListener("popstate", onPopState);
  }, []);

  const navigate = useCallback<RouterValue["navigate"]>((to, options) => {
    window.history[options?.replace ? "replaceState" : "pushState"](options?.state ?? null, "", to);
    setLocation({ pathname: window.location.pathname, state: options?.state ?? null });
    window.scrollTo({ top: 0 });
  }, []);
  const value = useMemo<RouterValue>(() => ({ ...location, navigate }), [location, navigate]);

  return <RouterContext.Provider value={value}>{children}</RouterContext.Provider>;
}

export function useLocation() {
  const router = useContext(RouterContext);
  if (!router) throw new Error("useLocation must be used inside RouterProvider");
  return { pathname: router.pathname, state: router.state };
}

export function useNavigate() {
  const router = useContext(RouterContext);
  if (!router) throw new Error("useNavigate must be used inside RouterProvider");
  return router.navigate;
}

export function Navigate({ to, replace = false, state }: { to: string; replace?: boolean; state?: unknown }) {
  const navigate = useNavigate();
  useEffect(() => navigate(to, { replace, state }), [navigate, replace, state, to]);
  return null;
}

export function Link({ to, onClick, ...props }: Omit<AnchorHTMLAttributes<HTMLAnchorElement>, "href"> & { to: string }) {
  const navigate = useNavigate();
  const follow = (event: MouseEvent<HTMLAnchorElement>) => {
    onClick?.(event);
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    navigate(to);
  };
  return <a {...props} href={to} onClick={follow} />;
}

export function NavLink({ to, end = false, children, ...props }: Omit<AnchorHTMLAttributes<HTMLAnchorElement>, "href"> & { to: string; end?: boolean }) {
  const { pathname } = useLocation();
  const active = end ? pathname === to : pathname === to || pathname.startsWith(`${to}/`);
  return <Link {...props} className={[props.className, active ? "active" : ""].filter(Boolean).join(" ")} to={to} aria-current={active ? "page" : undefined}>{children}</Link>;
}
