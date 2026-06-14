/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  theme: {
    extend: {
      colors: {
        'polytoria': {
          'bg': '#ffffff',
          'surface': '#f2f4f5',
          'surface-hover': '#e8ebed',
          'border': '#d4d8db',
          'border-light': '#e8ebed',
          'primary': '#00a2ff',
          'primary-hover': '#0090e0',
          'secondary': '#393b3d',
          'secondary-hover': '#2d2f31',
          'success': '#00b06f',
          'success-hover': '#009960',
          'danger': '#e2231a',
          'danger-hover': '#c91e16',
          'accent': '#00a2ff',
          'text': '#393b3d',
          'text-muted': '#606162',
          'text-dim': '#898b8d',
          'header': '#393b3d',
          'header-text': '#ffffff',
          'nav-bg': '#ffffff',
          'tab-active': '#00a2ff',
        }
      },
      fontFamily: {
        'sans': ['"Builder Sans"', '"Gotham SSm A"', '"Gotham SSm B"', '-apple-system', 'BlinkMacSystemFont', '"Segoe UI"', 'Roboto', 'sans-serif'],
        'mono': ['ui-monospace', 'SFMono-Regular', 'Menlo', 'Consolas', 'monospace'],
      },
      boxShadow: {
        'card': '0 1px 4px rgba(0, 0, 0, 0.06)',
        'card-hover': '0 4px 12px rgba(0, 0, 0, 0.1)',
        'header': '0 1px 4px rgba(0, 0, 0, 0.08)',
        'btn': '0 1px 3px rgba(0, 0, 0, 0.08)',
      },
      borderRadius: {
        'roblox': '8px',
      }
    },
  },
  plugins: [],
}
