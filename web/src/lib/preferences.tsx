// The signed-in user's preferences (PLT-17) for the whole app: formatting and theme.
import { useQuery } from '@tanstack/react-query';
import { createContext, useContext, useEffect, useMemo, type ReactNode } from 'react';
import { preferencesQuery } from '@/api/queries';
import { createFormatter, type Formatter } from './format';

export type Theme = 'system' | 'light' | 'dark';
const ThemeKey = 'paperdotnet.theme';

const FormatterContext = createContext<Formatter>(createFormatter());

/** Formats dates, times and numbers in the user's preferences. */
export function useFormat(): Formatter {
  return useContext(FormatterContext);
}

export function PreferencesProvider({ children }: { children: ReactNode }) {
  const { data } = useQuery(preferencesQuery);
  const formatter = useMemo(
    () =>
      createFormatter({
        language: data?.language ?? undefined,
        timeZone: data?.timeZone ?? undefined,
        dateFormat: data?.dateFormat ?? undefined,
        timeFormat: data?.timeFormat === '12h' ? '12h' : data?.timeFormat === '24h' ? '24h' : undefined,
        numberFormat: data?.numberFormat ?? undefined,
      }),
    [data],
  );

  useEffect(() => {
    if (data?.theme) applyTheme(data.theme as Theme);
  }, [data?.theme]);

  useEffect(() => {
    if (data?.language) document.documentElement.lang = data.language;
  }, [data?.language]);

  return <FormatterContext.Provider value={formatter}>{children}</FormatterContext.Provider>;
}

/** Applies a theme now and remembers it for the next page load (before the preferences are loaded). */
export function applyTheme(theme: Theme) {
  try {
    localStorage.setItem(ThemeKey, theme);
  } catch {
    // Storage can be unavailable (private windows); the theme still applies to this page.
  }

  const dark = theme === 'dark' || (theme === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.classList.toggle('dark', dark);
}

export function storedTheme(): Theme {
  try {
    const value = localStorage.getItem(ThemeKey);
    return value === 'light' || value === 'dark' ? value : 'system';
  } catch {
    return 'system';
  }
}
