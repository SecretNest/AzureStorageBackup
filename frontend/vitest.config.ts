import { defineConfig } from 'vitest/config'

// Only pure-logic tests (scopeRules) run here; they need no DOM environment, so jsdom is not pulled in.
export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
})
