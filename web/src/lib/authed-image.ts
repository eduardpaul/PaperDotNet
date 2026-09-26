// Images from the API (thumbnails, page images) need the Authorization header, which <img src> cannot send: they are
// fetched with the SDK's authenticated fetch and shown through object URLs.
import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { client } from '@/api/client';

export function blobQuery(path: string, enabled = true, version?: string | number) {
  return {
    queryKey: ['blob', path, version],
    enabled,
    staleTime: 5 * 60_000,
    retry: false,
    queryFn: async () => {
      const response = await client.fetch(new URL(path.replace(/^\/+/, ''), client.baseUrl + '/'));
      if (!response.ok) throw new Error(`${response.status}`);
      return response.blob();
    },
  };
}

/** An object URL for an API image; undefined while loading or when there is none (e.g. not processed yet). */
export function useAuthedImage(path: string | undefined, version?: string | number): { url?: string; failed: boolean } {
  const { data, isError } = useQuery(blobQuery(path ?? '', !!path, version));
  const [url, setUrl] = useState<string>();
  useEffect(() => {
    if (!data) return;
    const objectUrl = URL.createObjectURL(data);
    // An object URL is an external resource: created here and revoked by the cleanup (not derivable during render).
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setUrl(objectUrl);
    return () => URL.revokeObjectURL(objectUrl);
  }, [data]);
  return { url: data ? url : undefined, failed: isError };
}
