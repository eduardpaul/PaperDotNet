// The main navigation. Built-in screens register here like extension screens will (ADR-0033 §5).
import type { LucideIcon } from 'lucide-react';
import { Bell, CalendarDays, House, Inbox, ListChecks, Search, Stamp } from 'lucide-react';

export interface NavigationEntry {
  /** A route of the app. */
  to: '/' | '/inbox' | '/tasks' | '/calendar' | '/approvals' | '/search' | '/notifications';
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
  { to: '/tasks', label: 'My tasks', icon: ListChecks, shortcut: 't' },
  { to: '/calendar', label: 'Calendar', icon: CalendarDays, shortcut: 'c' },
  { to: '/approvals', label: 'Approvals', icon: Stamp, shortcut: 'a' },
  { to: '/search', label: 'Search', icon: Search, shortcut: 's' },
  { to: '/notifications', label: 'Notifications', icon: Bell, shortcut: 'n' },
];
