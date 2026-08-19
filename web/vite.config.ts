import { readFileSync } from 'node:fs'

import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// A LAN run serves the SPA over TLS, because `crypto.subtle` — and therefore the PKCE challenge
// the sign-in depends on — exists only in a secure context. The AppHost sets these two paths when
// it has been given a public host; without them this is the plain HTTP dev server it always was.
const certFile = process.env.DEV_TLS_CERT_FILE
const keyFile = process.env.DEV_TLS_KEY_FILE
const https =
  certFile && keyFile
    ? { cert: readFileSync(certFile), key: readFileSync(keyFile) }
    : undefined

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: { port: Number(process.env.PORT ?? 5173), strictPort: true, https },
  test: {
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
    css: true,
    globals: true,
  },
})
