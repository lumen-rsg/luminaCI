import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import { AuthProvider } from "./auth/AuthContext";
import { PreferencesProvider } from "./i18n/PreferencesContext";
import { RouterProvider } from "./lib/router";
import "./styles.css";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <PreferencesProvider>
      <RouterProvider>
        <AuthProvider><App /></AuthProvider>
      </RouterProvider>
    </PreferencesProvider>
  </StrictMode>
);
