import { createElement } from 'react';
import {
  BookUser,
  CalendarDays,
  FileText,
  FolderOpen,
  List,
  ListChecks,
  NotebookPen,
  type LucideIcon,
} from 'lucide-react';

/** The icon of a list (as a component type, for navigation registries): by template (documents, tasks, calendar, contacts, notes), else by kind. */
export function listIcon(list: { templateKey?: string | null; kind?: string | null }): LucideIcon {
  switch (list.templateKey) {
    case 'documents':
      return FileText;
    case 'tasks':
      return ListChecks;
    case 'calendar':
      return CalendarDays;
    case 'contacts':
      return BookUser;
    case 'notes':
      return NotebookPen;
    default:
      return list.kind === 'library' ? FolderOpen : List;
  }
}

/** The icon of a list as an element. */
export function ListIcon({
  list,
  className,
}: {
  list: { templateKey?: string | null; kind?: string | null };
  className?: string;
}) {
  // The icons are module constants, so this never creates a new component type.
  return createElement(listIcon(list), { className });
}
