import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { authApi } from "../lib/api";

type User = { username: string; role: string };
type AuthContextValue = {
  user: User | null;
  loading: boolean;
  login: (username: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
};

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  const resolveSession = useCallback(async () => {
    try {
      setUser(await authApi.me());
    } catch {
      try {
        const refreshed = await fetch("/api/auth/refresh", { method: "POST", credentials: "same-origin" });
        setUser(refreshed.ok ? await authApi.me() : null);
      } catch {
        setUser(null);
      }
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void resolveSession();
    const unauthorized = () => setUser(null);
    window.addEventListener("lumina:unauthorized", unauthorized);
    return () => window.removeEventListener("lumina:unauthorized", unauthorized);
  }, [resolveSession]);

  const value = useMemo<AuthContextValue>(() => ({
    user,
    loading,
    login: async (username, password) => setUser(await authApi.login(username, password)),
    logout: async () => {
      try { await authApi.logout(); } finally { setUser(null); }
    }
  }), [user, loading]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) throw new Error("useAuth must be used inside AuthProvider");
  return context;
}
