// Files: builds the multipart bodies of the generated upload calls and downloads files with their name and type.
// fetch cannot report upload progress; show an indeterminate indicator, or use XMLHttpRequest with `client.fetch`'s headers.
import { MultipartBody } from '@microsoft/kiota-abstractions';
import type { PaperDotNetClient } from './client.js';
import { throwProblem } from './errors.js';

/** File content: a browser File or Blob, or bytes. */
export type FileContent = Blob | ArrayBuffer | Uint8Array;

export interface UploadOptions {
  file: FileContent;
  /** Default: the name of a File, else `upload`. */
  fileName?: string;
  /** Default: the type of a Blob, else application/octet-stream. */
  contentType?: string;
  /** Document title (default: the file name). */
  title?: string;
  contentTypeId?: string;
  folderId?: string;
  /** OCR and text languages, e.g. ['eng', 'deu'] (default: the library's). */
  languages?: string | string[];
}

/**
 * The body of an upload call:
 * `await api.v10.workspaces.byWorkspaceId(w).lists.byListId(l).documents.post(await uploadBody({ file }))`.
 * The same body works for the personal and group inboxes and for replacing an item's file (only `file` and `languages`
 * are read there).
 */
export async function uploadBody(options: UploadOptions): Promise<MultipartBody> {
  const body = new MultipartBody();
  const file = options.file;
  const isBlob = typeof Blob !== 'undefined' && file instanceof Blob;
  const fileName = options.fileName ?? (isBlob && 'name' in file && typeof file.name === 'string' && file.name ? file.name : 'upload');
  const contentType = options.contentType ?? (isBlob && file.type ? file.type : 'application/octet-stream');
  body.addOrReplacePart('file', contentType, await toArrayBuffer(file), undefined, fileName);
  addText(body, 'title', options.title);
  addText(body, 'contentTypeId', options.contentTypeId);
  addText(body, 'folderId', options.folderId);
  addText(body, 'languages', Array.isArray(options.languages) ? options.languages.join('+') : options.languages);
  return body;
}

export interface DownloadedFile {
  blob: Blob;
  /** From Content-Disposition, when the server sent one. */
  fileName?: string;
  contentType: string;
}

/**
 * Downloads a file with its name and type (the generated `get()` of file endpoints returns only the bytes).
 * `path` is relative to the base URL, e.g. `/v1.0/workspaces/{w}/lists/{l}/items/{i}/file`.
 */
export async function downloadFile(client: PaperDotNetClient, path: string, init?: RequestInit): Promise<DownloadedFile> {
  const response = await client.fetch(new URL(path.replace(/^\/+/, ''), client.baseUrl + '/'), init);
  if (!response.ok) {
    await throwProblem(response);
  }

  const blob = await response.blob();
  return {
    blob,
    fileName: fileNameOf(response.headers.get('Content-Disposition')),
    contentType: response.headers.get('Content-Type') ?? blob.type,
  };
}

/** The file name of a Content-Disposition header (RFC 6266; `filename*` wins over `filename`). */
export function fileNameOf(contentDisposition: string | null | undefined): string | undefined {
  if (!contentDisposition) {
    return undefined;
  }

  const extended = /filename\*\s*=\s*([^']*)'[^']*'([^;]+)/i.exec(contentDisposition);
  if (extended) {
    try {
      return decodeURIComponent(extended[2].trim());
    } catch {
      // Fall back to the plain parameter.
    }
  }

  const plain = /filename\s*=\s*("(?:[^"\\]|\\.)*"|[^;]+)/i.exec(contentDisposition);
  if (!plain) {
    return undefined;
  }

  const value = plain[1].trim();
  return value.startsWith('"') ? value.slice(1, -1).replace(/\\(.)/g, '$1') : value;
}

function addText(body: MultipartBody, name: string, value: string | undefined): void {
  if (value !== undefined && value !== '') {
    body.addOrReplacePart(name, 'text/plain', value);
  }
}

async function toArrayBuffer(file: FileContent): Promise<ArrayBuffer> {
  if (file instanceof ArrayBuffer) {
    return file;
  }

  if (file instanceof Uint8Array) {
    // A copy: Kiota writes the whole underlying buffer, which is wrong for views.
    return file.slice().buffer as ArrayBuffer;
  }

  return file.arrayBuffer();
}
