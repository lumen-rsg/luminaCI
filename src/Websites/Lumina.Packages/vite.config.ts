import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "dist",
    sourcemap: true,
    target: "es2022"
  },
  server: {
    port: 5190,
    proxy: {
      "/api/package-index": {
        target: "https://packages.lumina.1t.ru",
        changeOrigin: true
      }
    }
  },
  test: {
    include: ["src/**/*.test.ts"]
  }
});
