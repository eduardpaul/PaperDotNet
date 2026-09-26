import { useInfiniteQuery, useQueries, useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { Briefcase, FileText, LogOut, Moon, Search, Sun } from 'lucide-react';
import {
  createContext,
  useCallback,
  useContext,
  useDeferredValue,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import { signOut } from '@/api/client';
import { listsQuery, workspacesQuery } from '@/api/queries';
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command';
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog';
import { Kbd } from '@/components/ui/feedback';
import { navigation } from '@/extensibility/navigation';
import { settingsPages } from '@/extensibility/settings';
import { ListIcon } from '@/features/lists/list-icon';
import { hitLink } from '@/features/search/hit-link';
import { searchQuery } from '@/features/search/queries';
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
  const lists = useQueries({ queries: (workspaces ?? []).map((w) => listsQuery(w.id!)) }).flatMap((q) => q.data ?? []);
  const workspaceName = new Map((workspaces ?? []).map((w) => [w.id, w.name]));
  const setTheme = useSetTheme();
  const [text, setText] = useState('');
  const query = useDeferredValue(text.trim());
  const hits = useInfiniteQuery({ ...searchQuery({ q: query }, 5), enabled: query.length >= 2 });
  const run = useCallback(
    (action: () => unknown) => () => {
      close();
      void action();
    },
    [close],
  );

  return (
    <Command loop>
      <CommandInput placeholder="Search, go to or run a command…" value={text} onValueChange={setText} />
      <CommandList>
        <CommandEmpty>Nothing found.</CommandEmpty>
        {query.length >= 2 && (
          <CommandGroup heading="Search" forceMount>
            <CommandItem
              forceMount
              value={`search ${query}`}
              onSelect={run(() => navigate({ to: '/search', search: { q: query } }))}
            >
              <Search />
              <span className="flex-1">
                Search for “<span className="font-medium">{query}</span>”
              </span>
            </CommandItem>
            {hits.data?.pages[0]?.value?.map((hit) => {
              const link = hitLink(hit);
              return (
                link && (
                  <CommandItem
                    key={`${hit.id}-${hit.page ?? ''}`}
                    forceMount
                    value={`hit ${hit.id} ${hit.page ?? ''}`}
                    onSelect={run(() => navigate(link))}
                  >
                    <FileText />
                    <span className="min-w-0 flex-1 truncate">{hit.title}</span>
                    {hit.page && <span className="text-xs text-muted">Page {hit.page}</span>}
                  </CommandItem>
                )
              );
            })}
          </CommandGroup>
        )}
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
        {lists.length > 0 && (
          <CommandGroup heading="Lists and libraries">
            {lists.map((list) => (
              <CommandItem
                key={list.id}
                value={`list ${list.name} ${workspaceName.get(list.workspaceId) ?? ''} ${list.id}`}
                onSelect={run(() =>
                  navigate({
                    to: '/w/$workspaceId/l/$listId',
                    params: { workspaceId: list.workspaceId!, listId: list.id! },
                  }),
                )}
              >
                <ListIcon list={list} />
                <span className="flex-1">{list.name}</span>
                <span className="text-xs text-muted">{workspaceName.get(list.workspaceId)}</span>
              </CommandItem>
            ))}
          </CommandGroup>
        )}
        <CommandGroup heading="Settings">
          {settingsPages.map((page) => (
            <CommandItem
              key={page.to}
              value={`settings ${page.label} ${page.keywords ?? ''}`}
              onSelect={run(() => navigate({ to: page.to }))}
            >
              <page.icon />
              {page.label}
            </CommandItem>
          ))}
        </CommandGroup>
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
