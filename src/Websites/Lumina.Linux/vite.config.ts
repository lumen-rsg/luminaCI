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
    port: 5191
  },
  test: {
    include: ["src/**/*.test.ts"]
  }
});
