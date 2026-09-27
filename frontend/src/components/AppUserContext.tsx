import { createContext, useContext } from 'react'
import type { AppUser } from '../api/types'

export const AppUserContext = createContext<AppUser | null>(null)
export const canReadAdministration = (user: AppUser) => user.roles.includes('Global Admin') || user.roles.includes('Global Reader Admin')
export const canManageAdministration = (user: AppUser) => user.roles.includes('Global Admin')
export const canManageOwnContent = (user: AppUser) => user.roles.includes('Global Admin') || user.roles.includes('User')
export function useAppUser() {
  const user = useContext(AppUserContext)
  if (!user) throw new Error('Application user is not loaded.')
  return user
}
