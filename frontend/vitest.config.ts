import { defineConfig } from 'vitest/config'

// 只跑纯逻辑测试（scopeRules），不需要 DOM 环境，因此不引 jsdom。
export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
})
