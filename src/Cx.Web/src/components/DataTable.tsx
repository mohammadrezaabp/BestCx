import { Children, cloneElement, isValidElement, useEffect, useState, type ReactElement, type ReactNode, type TableHTMLAttributes } from "react";
import Pagination from "./Pagination";

/** Shared presentation for every grid. Server-paged lists keep their own pager. */
export default function DataTable({ children, className = "", paginate = true, ...props }:
  TableHTMLAttributes<HTMLTableElement> & { paginate?: boolean }) {
  const [paging, setPaging] = useState({ page: 1, size: 20, source: "" });
  const sections = Children.toArray(children);
  const body = sections.find((child): child is ReactElement<{ children: ReactNode }> => isValidElement(child) && child.type === "tbody");
  const rows = Children.toArray(body?.props.children);
  const source = rows.map(row => isValidElement(row) ? row.key : "").join("|");
  useEffect(() => {
    setPaging(current => current.source === source ? current : { ...current, page: 1, source });
  }, [source]);
  const page = source === paging.source ? Math.min(paging.page, Math.max(1, Math.ceil(rows.length / paging.size))) : 1;
  return <div className="standard-grid">
    <div className="table-scroll" tabIndex={0} role="region" aria-label={props["aria-label"] ?? "Table of data"}>
      <table {...props} className={`data-grid ${className}`}>
        {sections.map(section => body && section === body && paginate
          ? cloneElement(body, {}, rows.slice((page - 1) * paging.size, page * paging.size))
          : section)}
      </table>
    </div>
    {paginate && rows.length > 0 && <Pagination page={page} pageSize={paging.size} total={rows.length}
      onPage={value => setPaging({ ...paging, page: value, source })}
      onPageSize={size => setPaging({ page: 1, size, source })} />}
  </div>;
}
