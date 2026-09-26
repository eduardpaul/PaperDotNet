// The main navigation. Built-in screens register here like extension screens will (ADR-0033 §5).
import type { LucideIcon } from 'lucide-react';
import { Bell, House, Inbox } from 'lucide-react';

export interface NavigationEntry {
  /** A route of the app. */
  to: '/' | '/inbox' | '/notifications';
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
  { to: '/notifications', label: 'Notifications', icon: Bell, shortcut: 'n' },
];
