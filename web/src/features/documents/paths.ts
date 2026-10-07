// API paths of a document's file (for images and downloads read with the authenticated fetch).
export function filePath(workspaceId: string, listId: string, itemId: string) {
  return `/v1.0/workspaces/${workspaceId}/lists/${listId}/items/${itemId}/file`;
}

export const thumbnailPath = (workspaceId: string, listId: string, itemId: string) =>
  `${filePath(workspaceId, listId, itemId)}/thumbnail`;

export const pageImagePath = (workspaceId: string, listId: string, itemId: string, page: number) =>
  `${filePath(workspaceId, listId, itemId)}/pages/${page}/image`;

/** Files the server reads (text, pages, OCR); libraries take any file and store others as they are (ADR-0047). */
export const isProcessable = (mediaType: string | null | undefined) =>
  ['application/pdf', 'image/tiff', 'image/jpeg', 'image/png', 'image/webp'].includes(mediaType ?? '');
