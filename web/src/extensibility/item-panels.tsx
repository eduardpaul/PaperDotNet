// Tabs of the item panel. Built-in features register here like extension panels will (ADR-0033 §5).
import type { ItemResponse, ListResponse } from '@paperdotnet/client';
import type { ComponentType } from 'react';
import { VersionsTab } from '@/features/lists/versions-tab';

export interface ItemPanelContext {
  workspaceId: string;
  list: ListResponse;
  item: ItemResponse;
}

export interface ItemPanel {
  key: string;
  label: string;
  applies: (context: ItemPanelContext) => boolean;
  component: ComponentType<ItemPanelContext>;
}

export const itemPanels: ItemPanel[] = [
  { key: 'versions', label: 'Versions', applies: ({ list }) => list.versioning !== 'off', component: VersionsTab },
];
