import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5174,
    strictPort: true,
    host: "0.0.0.0",
    proxy: {
      "/v1/portal": {
        target: process.env.CX_BACKEND_URL || "http://localhost:18080",
        changeOrigin: true,
      },
    },
  },
});
