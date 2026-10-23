import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Link, useLocation } from "react-router-dom";
import { operations } from "../operationsApi";
import { fmtDateTime, fmtNumber } from "../format";
import { ErrorBox, Loading, useAsync } from "./Common";
import Icon from "./Icon";

export default function NotificationBell() {
  const [unread, setUnread] = useState(0);
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  const popup = useRef<HTMLDivElement>(null);
  const popupId = useId();
  const location = useLocation();
  const [position, setPosition] = useState({ top: 0, left: 0, width: 360, maxHeight: 480 });
  const preview = useAsync(() => open ? operations.notificationGrid({ page: 1, pageSize: 5 }) : Promise.resolve(null), [open]);

  useEffect(() => setOpen(false), [location.key]);

  useLayoutEffect(() => {
    if (!open) return;
    const place = () => {
      const rect = trigger.current!.getBoundingClientRect();
      const width = Math.min(360, window.innerWidth - 24);
      const top = rect.bottom + 8;
      setPosition({ top, width, left: Math.max(12, Math.min(rect.left, window.innerWidth - width - 12)),
        maxHeight: Math.min(480, Math.max(120, window.innerHeight - top - 12)) });
    };
    const outside = (event: PointerEvent) => {
      if (!trigger.current?.contains(event.target as Node) && !popup.current?.contains(event.target as Node)) setOpen(false);
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key === "Escape") { setOpen(false); trigger.current?.focus(); }
    };
    const focusOutside = (event: FocusEvent) => {
      if (!trigger.current?.contains(event.target as Node) && !popup.current?.contains(event.target as Node)) setOpen(false);
    };
    place();
    popup.current?.focus();
    document.addEventListener("pointerdown", outside);
    document.addEventListener("keydown", escape);
    document.addEventListener("focusin", focusOutside);
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      document.removeEventListener("pointerdown", outside);
      document.removeEventListener("keydown", escape);
      document.removeEventListener("focusin", focusOutside);
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [open]);

  useEffect(() => {
    let active = true;
    let pending = false;
    const refreshCount = async () => {
      if (pending) return;
      pending = true;
      try {
        const result = await operations.unreadNotifications();
        if (active) setUnread(result.count);
      } catch { /* Opening the preview exposes loading errors and a retry. */ }
      finally { pending = false; }
    };
    const refresh = () => { void refreshCount(); if (open) preview.reload(); };
    void refreshCount();
    const timer = window.setInterval(() => { if (!document.hidden) refresh(); }, 30000);
    window.addEventListener("notifications-changed", refresh);
    window.addEventListener("focus", refresh);
    return () => { active = false; window.clearInterval(timer); window.removeEventListener("notifications-changed", refresh); window.removeEventListener("focus", refresh); };
  }, [open, preview.reload]);

  function viewNotification(id: string, readAt: string | null) {
    setOpen(false);
    if (!readAt) void operations.readNotification(id)
      .then(() => window.dispatchEvent(new Event("notifications-changed")))
      .catch(() => { /* Keep the unread badge if updating the read state fails. */ });
  }

  return <>
    <button type="button" ref={trigger} className="icon-button notification-bell" aria-expanded={open} aria-controls={popupId}
      aria-label={unread ? `Notifications; ${fmtNumber(unread)} Unread` : "Notifications"} title="Notifications" onClick={() => setOpen(value => !value)}>
      <Icon name="bell" size={22} />{unread > 0 && <span className="notification-count" aria-hidden="true">{unread > 99 ? "99+" : fmtNumber(unread)}</span>}
    </button>
    {open && createPortal(<div ref={popup} id={popupId} className="notification-popover" role="region" tabIndex={-1} aria-label="Recent announcements" style={position}>
      <header><strong>Notifications</strong>{unread > 0 && <span>{fmtNumber(unread)} Unread</span>}</header>
      <div className="notification-preview-list" aria-busy={preview.loading}>
        {preview.loading ? <Loading /> : preview.error ? <div className="notification-preview-state"><ErrorBox error={preview.error} /><button onClick={preview.reload}>Retry</button></div>
          : !preview.data?.items.length ? <div className="notification-preview-state"><Icon name="bell" size={26} /><p>You have no new notification.</p></div>
          : preview.data.items.map(item => <Link className={`notification-preview-item${item.read_at ? "" : " unread"}`} key={item.id}
            to={item.link} onClick={() => viewNotification(item.id, item.read_at)}>
            <span className="notification-preview-dot" aria-label={item.read_at ? "Read" : "Unread"} />
            <div><strong>{item.title}</strong><p>{item.body}</p><time>{fmtDateTime(item.created_at)}</time></div>
          </Link>)}
      </div>
      <footer><Link to="/notifications" onClick={() => setOpen(false)}>View all <Icon name="arrow" size={15} /></Link></footer>
    </div>, document.body)}
  </>;
}
