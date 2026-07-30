import { useAuth } from "./auth/AuthContext";
import { AppLoading, AppShell } from "./components/AppShell";
import { Navigate, useLocation } from "./lib/router";
import { BuildDetail } from "./pages/BuildDetail";
import { Builds } from "./pages/Builds";
import { Dashboard } from "./pages/Dashboard";
import { Login } from "./pages/Login";
import { Pipelines } from "./pages/Pipelines";
import { Repositories } from "./pages/Repositories";
import { Security } from "./pages/Security";
import { Sources } from "./pages/Sources";

function ProtectedApp() {
  const { user, loading } = useAuth();
  const { pathname } = useLocation();
  if (loading) return <AppLoading />;
  if (!user) return <Navigate to="/login" state={{ from: pathname }} replace />;

  let page;
  if (pathname === "/") page = <Dashboard />;
  else if (pathname === "/builds") page = <Builds />;
  else if (/^\/builds\/[^/]+$/.test(pathname)) page = <BuildDetail />;
  else if (pathname === "/pipelines") page = <Pipelines />;
  else if (pathname === "/sources") page = <Sources />;
  else if (pathname === "/repositories") page = <Repositories />;
  else if (pathname === "/security") page = <Security />;
  else page = <Navigate to="/" replace />;

  return <AppShell>{page}</AppShell>;
}

export default function App() {
  const { pathname } = useLocation();
  return pathname === "/login" ? <Login /> : <ProtectedApp />;
}
