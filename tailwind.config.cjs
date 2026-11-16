// CC Switch v7 design tokens, trimmed to the configuration UI.
module.exports = {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  darkMode: ["selector", ".dark"],
  theme: {
    extend: {
      colors: {
        app: "var(--bg-app)",
        sidebar: "var(--bg-sidebar)",
        surface: "var(--bg-card)",
        subtle: "var(--bg-subtle)",
        selected: "var(--bg-selected)",
        border: "hsl(var(--border))",
        "border-strong": "var(--border-strong)",
        ring: "hsl(var(--ring))",
        fg: { 1: "var(--text-1)", 2: "var(--text-2)", 3: "var(--text-3)" },
        action: {
          DEFAULT: "var(--action-bg)",
          fg: "var(--action-fg)",
          hover: "var(--action-hover)",
          text: "var(--action-text)",
          soft: "var(--action-soft)",
        },
        success: {
          DEFAULT: "#16a34a",
          text: "var(--success-text)",
          soft: "var(--success-soft)",
        },
        danger: "#dc2626",
        overlay: "var(--overlay)",
      },
      borderColor: { DEFAULT: "hsl(var(--border))" },
      borderRadius: { control: "6px", panel: "10px", dialog: "14px" },
      boxShadow: {
        "v7-sm": "0 1px 2px #1018280f",
        "v7-md": "0 4px 12px #10182814",
        "v7-lg": "0 20px 40px #10182829",
      },
      fontSize: {
        body: ["13px", "20px"],
        caption: ["12px", "18px"],
        title: ["18px", "26px"],
        section: ["14px", "22px"],
      },
      spacing: { control: "36px", "control-sm": "30px" },
    },
  },
  plugins: [require("tailwindcss-animate")],
};
