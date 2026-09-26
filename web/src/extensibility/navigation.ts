// The main navigation. Built-in screens register here like extension screens will (ADR-0033 §5).
import type { LucideIcon } from 'lucide-react';
import { Bell, House, Inbox, Search } from 'lucide-react';

export interface NavigationEntry {
  /** A route of the app. */
  to: '/' | '/inbox' | '/search' | '/notifications';
  label: string;
  icon: LucideIcon;
  /** Keyboard shortcut after "g" (e.g. g h for Home). */
  shortcut?: string;
  /** Match only this exact path (Home). */
  exact?: boolean;
}

export const navigation: NavigationEntry[] = [
  { to: '/', label: 'Home', icon: House, shortcut: 'h', exact: true },
  { to: '/inbox', label: 'Inbox', icon: Inbox, shortcut: 'i' },
  { to: '/search', label: 'Search', icon: Search, shortcut: 's' },
  { to: '/notifications', label: 'Notifications', icon: Bell, shortcut: 'n' },
];
