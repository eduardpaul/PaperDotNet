// The pages of Settings. Built-in pages register here like extension pages will (ADR-0033 §5).
import type { LucideIcon } from 'lucide-react';
import { BellRing, CalendarSync, KeyRound, ShieldCheck, SlidersHorizontal, UserRound } from 'lucide-react';

export interface SettingsPage {
  to:
    | '/settings'
    | '/settings/preferences'
    | '/settings/security'
    | '/settings/notifications'
    | '/settings/tokens'
    | '/settings/calendar-feeds';
  label: string;
  icon: LucideIcon;
  /** Words the command palette also matches. */
  keywords?: string;
}

export const settingsPages: SettingsPage[] = [
  { to: '/settings', label: 'Profile', icon: UserRound, keywords: 'name account' },
  {
    to: '/settings/preferences',
    label: 'Preferences',
    icon: SlidersHorizontal,
    keywords: 'language time zone date format theme ocr',
  },
  { to: '/settings/security', label: 'Password and passkeys', icon: ShieldCheck, keywords: 'security sign-in' },
  {
    to: '/settings/notifications',
    label: 'Notifications',
    icon: BellRing,
    keywords: 'webhook quiet hours digest following',
  },
  { to: '/settings/tokens', label: 'API tokens', icon: KeyRound, keywords: 'access token cli integration' },
  { to: '/settings/calendar-feeds', label: 'Calendar feeds', icon: CalendarSync, keywords: 'ics subscribe' },
];
