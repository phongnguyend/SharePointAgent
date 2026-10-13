# Dialogs

- Use `src/components/Modal.tsx` for all application dialogs. It provides shared Header, Body, and Footer sections, styling, accessible naming, and focus handling. The `ui.tsx` export is a compatibility re-export.
- Put dialog actions in the `footer` prop; put optional preview controls in `headerActions`. Put content in children, which render in the Body section.
- Do not call browser `alert`, `confirm`, or `prompt`, including `window` and `globalThis` variants. Use a controlled Modal for confirmations and text entry, and Toast for transient notifications.
- Do not create standalone `<dialog>` elements outside `Modal.tsx`. The shared component uses the native element only for accessibility and modal behavior, with application styling.

# Notifications

- Use `useToast` from `components/Toast` for transient success, information, warning, and error messages. Do not create page-specific success banners or notification timers.
- Keep form validation, persistent loading/configuration state, and errors needed for correction inline. Do not toast routine background refresh failures repeatedly.
- Use `useCopy` for clipboard actions so success and failure feedback is consistent. Do not put copied content or credentials into toast messages.
