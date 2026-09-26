import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { LogOut, Monitor, Moon, Sun } from 'lucide-react';
import { api, signOut } from '@/api/client';
import { keys } from '@/api/keys';
import { meQuery, preferencesQuery } from '@/api/queries';
import { Avatar } from '@/components/ui/avatar';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/menu';
import { applyTheme, storedTheme, type Theme } from '@/lib/preferences';

export function useSetTheme() {
  const queryClient = useQueryClient();
  const { data: preferences } = useQuery(preferencesQuery);
  return useMutation({
    mutationFn: (theme: Theme) => api.v10.me.preferences.patch({ theme }, ifMatch(preferences)),
    onMutate: (theme) => applyTheme(theme),
    onSuccess: (updated) => queryClient.setQueryData(keys.preferences, updated),
    onError: () => queryClient.invalidateQueries({ queryKey: keys.preferences }),
  });
}

export function UserMenu() {
  const { data: me } = useQuery(meQuery);
  const { data: preferences } = useQuery(preferencesQuery);
  const setTheme = useSetTheme();
  const theme = (preferences?.theme as Theme | undefined) ?? storedTheme();

  return (
    <DropdownMenu>
      <DropdownMenuTrigger className="rounded-full p-0.5 hover:bg-surface-muted" aria-label="Account">
        <Avatar name={me?.displayName ?? me?.userName} />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-60">
        <div className="px-2 py-2">
          <p className="truncate text-[13px] font-medium">{me?.displayName ?? me?.userName}</p>
          <p className="truncate text-xs text-muted">{me?.email ?? me?.userName}</p>
        </div>
        <DropdownMenuSeparator />
        <DropdownMenuLabel>Theme</DropdownMenuLabel>
        <DropdownMenuRadioGroup value={theme} onValueChange={(value) => setTheme.mutate(value as Theme)}>
          <DropdownMenuRadioItem value="system">
            <Monitor className="size-4 text-muted" /> System
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="light">
            <Sun className="size-4 text-muted" /> Light
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="dark">
            <Moon className="size-4 text-muted" /> Dark
          </DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => void signOut()}>
          <LogOut /> Sign out
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
