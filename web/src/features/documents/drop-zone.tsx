import { Upload } from 'lucide-react';
import { useRef, useState, type ReactNode } from 'react';
import { Button, type ButtonProps } from '@/components/ui/button';
import { cn } from '@/lib/utils';

/** Accepts files dropped anywhere on its area, with a clear hint of where they go. */
export function DropZone({
  onFiles,
  label,
  disabled,
  children,
  className,
}: {
  onFiles: (files: File[]) => void;
  label: string;
  disabled?: boolean;
  children: ReactNode;
  className?: string;
}) {
  const [active, setActive] = useState(false);
  const depth = useRef(0);
  const hasFiles = (e: React.DragEvent) => Array.from(e.dataTransfer.types).includes('Files');

  return (
    <div
      className={cn('relative', className)}
      onDragEnter={(e) => {
        if (disabled || !hasFiles(e)) return;
        e.preventDefault();
        depth.current++;
        setActive(true);
      }}
      onDragOver={(e) => {
        if (!disabled && hasFiles(e)) e.preventDefault();
      }}
      onDragLeave={() => {
        if (--depth.current <= 0) setActive(false);
      }}
      onDrop={(e) => {
        if (disabled || !hasFiles(e)) return;
        e.preventDefault();
        depth.current = 0;
        setActive(false);
        const files = Array.from(e.dataTransfer.files);
        if (files.length) onFiles(files);
      }}
    >
      {children}
      {active && (
        <div className="pointer-events-none absolute inset-0 z-20 flex items-center justify-center rounded-xl border-2 border-dashed border-accent bg-accent-soft/80 backdrop-blur-[1px]">
          <p className="flex items-center gap-2 text-sm font-semibold text-accent">
            <Upload className="size-5" /> {label}
          </p>
        </div>
      )}
    </div>
  );
}

/** A button that opens the file picker. */
export function FilePickerButton({
  accept,
  multiple = true,
  onFiles,
  ...props
}: Omit<ButtonProps, 'onClick'> & { accept: string; multiple?: boolean; onFiles: (files: File[]) => void }) {
  const input = useRef<HTMLInputElement>(null);
  return (
    <>
      <input
        ref={input}
        type="file"
        accept={accept}
        multiple={multiple}
        hidden
        onChange={(e) => {
          const files = Array.from(e.target.files ?? []);
          e.target.value = '';
          if (files.length) onFiles(files);
        }}
      />
      <Button {...props} onClick={() => input.current?.click()} />
    </>
  );
}
