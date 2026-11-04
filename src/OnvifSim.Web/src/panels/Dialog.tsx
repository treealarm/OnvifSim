import { useEffect, useId, useRef, type ReactNode } from 'react';

// A modal dialog using the browser's own <dialog>: Escape and focus handling come with it. The parent
// owns the open flag; closing by any route calls onClose.
export function Dialog({
  open,
  title,
  onClose,
  children,
  actions,
  className,
}: {
  open: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  actions?: ReactNode;
  // An extra class on the dialog itself, for one that needs more room than the default.
  className?: string;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    else if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog
      ref={ref}
      className={className}
      aria-labelledby={titleId}
      onClose={onClose}
      onCancel={(e) => {
        e.preventDefault();
        onClose();
      }}
    >
      <h2 id={titleId}>{title}</h2>
      {children}
      {actions && <div className="dialog-actions">{actions}</div>}
    </dialog>
  );
}
