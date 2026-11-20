import js from "@eslint/js";
import globals from "globals";

export default [
  js.configs.recommended,
  {
    files: ["web/**/*.js"],
    languageOptions: { ecmaVersion: 2022, sourceType: "script", globals: { ...globals.browser } },
    rules: {
      "no-var": "off",
      "no-empty": ["error", { allowEmptyCatch: true }],
      "no-unused-vars": ["error", { caughtErrors: "none" }],
    },
  },
  {
    files: ["tests/**/*.mjs", "eslint.config.js"],
    languageOptions: { ecmaVersion: 2022, sourceType: "module", globals: { ...globals.node } },
  },
];
