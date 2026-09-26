// API paths of a document's file (for images and downloads read with the authenticated fetch).
export function filePath(workspaceId: string, listId: string, itemId: string) {
  return `/v1.0/workspaces/${workspaceId}/lists/${listId}/items/${itemId}/file`;
}

export const thumbnailPath = (workspaceId: string, listId: string, itemId: string) =>
  `${filePath(workspaceId, listId, itemId)}/thumbnail`;

export const pageImagePath = (workspaceId: string, listId: string, itemId: string, page: number) =>
  `${filePath(workspaceId, listId, itemId)}/pages/${page}/image`;

/** Uploads the server accepts (DOC-01); the server checks the content, this only filters the file picker. */
export const acceptedTypes = 'application/pdf,image/tiff,image/jpeg,image/png,.pdf,.tif,.tiff,.jpg,.jpeg,.png';
