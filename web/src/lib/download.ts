/** Saves bytes from the API as a file (exports, templates): the browser's download of a temporary object URL. */
export function saveBytes(bytes: ArrayBuffer, fileName: string, type: string) {
  const url = URL.createObjectURL(new Blob([bytes], { type }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}
