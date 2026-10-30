import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests/browser",
  reporter: "list",
  use: { browserName: "chromium", headless: true }
});
