import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { Briefcase, LogOut, Moon, Sun } from 'lucide-react';
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { signOut } from '@/api/client';
import { workspacesQuery } from '@/api/queries';
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command';
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog';
import { Kbd } from '@/components/ui/feedback';
import { navigation } from '@/extensibility/navigation';
import { useSetTheme } from './user-menu';

const PaletteContext = createContext<{ open: () => void }>({ open: () => {} });

export function useCommandPalette() {
  return useContext(PaletteContext);
}

/** ⌘K / Ctrl+K: go anywhere and run actions from the keyboard; "g" then a letter jumps to a main screen. */
export function CommandPaletteProvider({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const navigate = useNavigate();

  useEffect(() => {
    let pendingG = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const onKeyDown = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        setOpen((value) => !value);
        return;
      }

      const target = event.target as HTMLElement | null;
      if (
        event.metaKey ||
        event.ctrlKey ||
        event.altKey ||
        target?.closest('input, textarea, select, [contenteditable]')
      )
        return;
      if (pendingG) {
        pendingG = false;
        const entry = navigation.find((n) => n.shortcut === event.key);
        if (entry) {
          event.preventDefault();
          void navigate({ to: entry.to });
        }
      } else if (event.key === 'g') {
        pendingG = true;
        clearTimeout(timer);
        timer = setTimeout(() => (pendingG = false), 1000);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      clearTimeout(timer);
    };
  }, [navigate]);

  const value = useMemo(() => ({ open: () => setOpen(true) }), []);
  return (
    <PaletteContext.Provider value={value}>
      {children}
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent hideClose className="max-w-xl overflow-hidden p-0" aria-describedby={undefined}>
          <DialogTitle className="sr-only">Command palette</DialogTitle>
          {open && <Palette close={() => setOpen(false)} />}
        </DialogContent>
      </Dialog>
    </PaletteContext.Provider>
  );
}

function Palette({ close }: { close: () => void }) {
  const navigate = useNavigate();
  const { data: workspaces } = useQuery(workspacesQuery);
  const setTheme = useSetTheme();
  const run = useCallback(
    (action: () => unknown) => () => {
      close();
      void action();
    },
    [close],
  );

  return (
    <Command loop>
      <CommandInput placeholder="Type a command or go to…" />
      <CommandList>
        <CommandEmpty>Nothing found.</CommandEmpty>
        <CommandGroup heading="Go to">
          {navigation.map((entry) => (
            <CommandItem key={entry.to} value={`go ${entry.label}`} onSelect={run(() => navigate({ to: entry.to }))}>
              <entry.icon />
              <span className="flex-1">{entry.label}</span>
              {entry.shortcut && (
                <span className="flex gap-1">
                  <Kbd>G</Kbd>
                  <Kbd>{entry.shortcut.toUpperCase()}</Kbd>
                </span>
              )}
            </CommandItem>
          ))}
        </CommandGroup>
        {!!workspaces?.length && (
          <CommandGroup heading="Workspaces">
            {workspaces
              .filter((w) => !w.isPersonal)
              .map((workspace) => (
                <CommandItem
                  key={workspace.id}
                  value={`workspace ${workspace.name} ${workspace.id}`}
                  onSelect={run(() => navigate({ to: '/w/$workspaceId', params: { workspaceId: workspace.id! } }))}
                >
                  <Briefcase />
                  {workspace.name}
                </CommandItem>
              ))}
          </CommandGroup>
        )}
        <CommandGroup heading="Actions">
          <CommandItem value="theme light" onSelect={run(() => setTheme.mutate('light'))}>
            <Sun /> Switch to light theme
          </CommandItem>
          <CommandItem value="theme dark" onSelect={run(() => setTheme.mutate('dark'))}>
            <Moon /> Switch to dark theme
          </CommandItem>
          <CommandItem value="sign out" onSelect={run(signOut)}>
            <LogOut /> Sign out
          </CommandItem>
        </CommandGroup>
      </CommandList>
    </Command>
  );
}
