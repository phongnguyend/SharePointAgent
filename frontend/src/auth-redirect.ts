import { broadcastResponseToMainFrame } from '@azure/msal-browser/redirect-bridge'

broadcastResponseToMainFrame().catch(() => {
  document.getElementById('status')!.textContent = 'Sign-in could not complete. Return to SharePoint Agent and try again.'
})
