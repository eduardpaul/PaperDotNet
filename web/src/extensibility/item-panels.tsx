// Tabs of the item panel. Built-in features register here like extension panels will (ADR-0033 §5).
import type { ItemResponse, ListResponse } from '@paperdotnet/client';
import type { ComponentType } from 'react';
import { SeriesTab } from '@/features/calendar/series-tab';
import { ActivityTab } from '@/features/collaboration/activity-tab';
import { PreviewTab } from '@/features/documents/preview-tab';
import { AccessTab } from '@/features/list-settings/access-tab';
import { VersionsTab } from '@/features/lists/versions-tab';
import { NoteLinksTab } from '@/features/notes/links-tab';
import { ChecklistTab, RelatedTab, TaskRepeatTab } from '@/features/tasks/task-panels';

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

const isTask = ({ list, item }: ItemPanelContext) =>
  !item.isFolder && !!list.contentTypes?.find((c) => c.id === item.contentTypeId && c.key === 'task');
const isNote = ({ list, item }: ItemPanelContext) =>
  !item.isFolder && !!list.contentTypes?.find((c) => c.id === item.contentTypeId && c.key === 'note');
const isEvent = ({ list, item }: ItemPanelContext) =>
  !item.isFolder && !!list.contentTypes?.find((c) => c.id === item.contentTypeId && c.key === 'event');

export const itemPanels: ItemPanel[] = [
  { key: 'checklist', label: 'Checklist', applies: isTask, component: ChecklistTab },
  { key: 'related', label: 'Related', applies: isTask, component: RelatedTab },
  { key: 'repeat', label: 'Repeat', applies: isTask, component: TaskRepeatTab },
  { key: 'series', label: 'Repeat', applies: isEvent, component: SeriesTab },
  { key: 'links', label: 'Links', applies: isNote, component: NoteLinksTab },
  { key: 'activity', label: 'Activity', applies: ({ item }) => !item.isFolder, component: ActivityTab },
  {
    key: 'preview',
    label: 'Preview',
    applies: ({ list, item }) => list.kind === 'library' && !item.isFolder,
    component: PreviewTab,
  },
  { key: 'versions', label: 'Versions', applies: ({ list }) => list.versioning !== 'off', component: VersionsTab },
  { key: 'access', label: 'Access', applies: () => true, component: AccessTab },
];
