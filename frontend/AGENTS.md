# Dialogs

- Use `src/components/Modal.tsx` for all application dialogs. It provides shared Header, Body, and Footer sections, styling, accessible naming, and focus handling. The `ui.tsx` export is a compatibility re-export.
- Put dialog actions in the `footer` prop; put optional preview controls in `headerActions`. Put content in children, which render in the Body section.
- Do not call browser `alert`, `confirm`, or `prompt`, including `window` and `globalThis` variants. Use a controlled Modal for notifications, confirmations, and text entry.
- Do not create standalone `<dialog>` elements outside `Modal.tsx`. The shared component uses the native element only for accessibility and modal behavior, with application styling.
