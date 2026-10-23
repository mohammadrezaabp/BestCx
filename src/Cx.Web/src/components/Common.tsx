export { useAsync, useCursorPaging } from "../hooks/useAsync";
import { ApiError } from "../api";
import { fmtNumber, statusLabels } from "../format";
import Icon from "./Icon";
import { useContext } from "react";
import { PageChrome } from "./PageChrome";

export function Badge({
  value,
  map,
  prefix,
}: {
  value?: string | null;
  map?: Record<string, string>;
  prefix?: string;
}) {
  if (!value) return <span className="muted">—</span>;
  return (
    <span className={`badge ${value}`}>
      {prefix && `${prefix}: `}
      {map?.[value] ?? statusLabels[value] ?? value}
    </span>
  );
}

export function ErrorBox({ error }: { error: unknown }) {
  if (!error) return null;
  const message =
    error instanceof ApiError
      ? error.status === 401
        ? "Your session is not valid. Log in again."
        : error.status === 403
          ? "You do not have the required access for this operation."
          : error.message
      : error instanceof TypeError
        ? "Communication with the server could not be established. Check the connection and try again."
        : error instanceof Error
          ? error.message
          : String(error);

  return (
    <div className="error" role="alert">
      {message}
    </div>
  );
}

export function Empty({ text = "There is no data to display" }: { text?: string }) {
  return (
    <div className="empty">
      <Icon name="inbox" size={30} />
      <div>{text}</div>
    </div>
  );
}

export function Loading() {
  return (
    <div className="empty loading" role="status">
      <span className="spinner" />
      Loading…
    </div>
  );
}

export function PageHead({
  title,
  subtitle,
  actions,
}: {
  title: string;
  subtitle?: string;
  actions?: React.ReactNode;
}) {
  const chrome = useContext(PageChrome);
  return (
    <div className="page-head">
      <div>
        <h1>{title}</h1>
        {subtitle && <p>{subtitle}</p>}
      </div>
      <div className="page-head-actions">{actions && <div className="toolbar">{actions}</div>}{chrome}</div>
    </div>
  );
}

export function Pager({
  hasMore,
  canGoBack,
  onNext,
  onBack,
  page,
  loading = false,
  pageSize,
  onPageSize,
}: {
  hasMore: boolean;
  canGoBack: boolean;
  onNext: () => void;
  onBack: () => void;
  page?: number;
  loading?: boolean;
  pageSize?: number;
  onPageSize?: (size: number) => void;
}) {
  if (!page && !hasMore && !canGoBack) return null;
  return (
    <nav className="pagination" aria-label="Pagination">
      {page && <span className="pagination-summary">Page {fmtNumber(page)}</span>}
      <div className="pagination-pages">
        <button onClick={onBack} disabled={!canGoBack || loading}>
          Previous page
        </button>
        <button onClick={onNext} disabled={!hasMore || loading}>
          Next page
        </button>
      </div>
      {pageSize && onPageSize && <label className="pagination-size"><span>Rows per page</span><select disabled={loading} value={pageSize} onChange={event => onPageSize(Number(event.target.value))}>
        {[...new Set([10, 20, 50, pageSize])].sort((a, b) => a - b).map(size => <option value={size} key={size}>{fmtNumber(size)}</option>)}
      </select></label>}
    </nav>
  );
}

/** Horizontal bar list — used instead of a chart library to keep the bundle small. */
export function BarList({
  items,
}: {
  items: { name: string; count: number }[];
}) {
  if (items.length === 0) return <Empty text="An unregistered case" />;
  const max = Math.max(...items.map((i) => i.count), 1);

  return (
    <div>
      {items.map((item) => (
        <div className="bar-row" key={item.name}>
          <span title={item.name}>{item.name}</span>
          <div className="bar-track">
            <div className="bar" style={{ width: `${item.count ? Math.max(4, (item.count / max) * 100) : 0}%` }} />
          </div>
          <span style={{ textAlign: "end" }}>{fmtNumber(item.count)}</span>
        </div>
      ))}
    </div>
  );
}
