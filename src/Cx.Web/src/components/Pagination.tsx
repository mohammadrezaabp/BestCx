import { fmtNumber } from "../format";

/** 1 … 4 5 [6] 7 8 … 20 — the current page with two neighbours and both ends. */
function pageList(current: number, last: number): (number | "gap")[] {
  const pages = new Set([1, last, current - 2, current - 1, current, current + 1, current + 2]);
  const sorted = [...pages].filter((p) => p >= 1 && p <= last).sort((a, b) => a - b);
  const result: (number | "gap")[] = [];
  sorted.forEach((page, index) => {
    if (index > 0 && page - sorted[index - 1] > 1) result.push("gap");
    result.push(page);
  });
  return result;
}

export default function Pagination({
  page,
  pageSize,
  total,
  onPage,
  onPageSize,
  sizes = [10, 20, 50],
  unit = "",
  sizeLabel = "Rows per page",
}: {
  page: number;
  pageSize: number;
  total: number;
  onPage: (page: number) => void;
  onPageSize?: (size: number) => void;
  sizes?: number[];
  unit?: string;
  sizeLabel?: string;
}) {
  const last = Math.max(1, Math.ceil(total / pageSize));
  const first = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const end = Math.min(total, page * pageSize);
  return (
    <nav className="pagination" aria-label="Pagination">
      <span className="pagination-summary">
        {total === 0
          ? "No results"
          : `${fmtNumber(first)} to ${fmtNumber(end)} of ${fmtNumber(total)}${unit ? ` ${unit}` : ""}`}
      </span>
      {(
        <div className="pagination-pages">
          <button
            onClick={() => onPage(page - 1)}
            disabled={page <= 1}
            aria-label="Previous page"
          >
            ‹
          </button>
          {pageList(page, last).map((item, index) =>
            item === "gap" ? (
              <span key={`gap-${index}`} className="pagination-gap">
                …
              </span>
            ) : (
              <button
                key={item}
                className={item === page ? "active" : ""}
                aria-current={item === page ? "page" : undefined}
                onClick={() => onPage(item)}
              >
                {fmtNumber(item)}
              </button>
            ),
          )}
          <button
            onClick={() => onPage(page + 1)}
            disabled={page >= last}
            aria-label="Next page"
          >
            ›
          </button>
        </div>
      )}
      {onPageSize && (
        <label className="pagination-size">
          <span>{sizeLabel}</span>
          <select
            value={pageSize}
            onChange={(event) => onPageSize(Number(event.target.value))}
          >
            {sizes.map((size) => (
              <option key={size} value={size}>
                {fmtNumber(size)}
              </option>
            ))}
          </select>
        </label>
      )}
    </nav>
  );
}
