import { createFileRoute } from '@tanstack/react-router';
import { ContentScreen } from '@/features/content/content-screen';

export interface ContentSearch {
  workspace?: string;
  collection?: string;
  folder?: string;
  item?: string;
  surface?: 'head' | 'published';
  archive?: 'active' | 'archived' | 'all';
  q?: string;
}
const text = (value: unknown) => (typeof value === 'string' && value ? value : undefined);
export const Route = createFileRoute('/_app/content')({
  validateSearch: (value: Record<string, unknown>): ContentSearch => ({
    workspace: text(value.workspace),
    collection: text(value.collection),
    folder: text(value.folder),
    item: text(value.item),
    surface: value.surface === 'head' ? 'head' : 'published',
    archive: value.archive === 'archived' || value.archive === 'all' ? value.archive : 'active',
    q: text(value.q),
  }),
  component: ContentScreen,
});
