import { useEffect, useId, useState } from "react";
import { api, type CustomerIdentity } from "../api";
import { ErrorBox } from "./Common";
import Icon from "./Icon";

export default function CustomerSelect({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const listId = useId();
  const [selected, setSelected] = useState<CustomerIdentity | null>(null);
  const [term, setTerm] = useState("");
  const [open, setOpen] = useState(false);
  const [results, setResults] = useState<CustomerIdentity[]>([]);
  const [active, setActive] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    if (!value) { setSelected(null); return; }
    if (selected?.customer_id === value) return;
    let cancelled = false;
    api.customer(value).then(detail => {
      if (!cancelled) { setSelected(detail.customer); setError(null); }
    }).catch(err => { if (!cancelled) setError(err); });
    return () => { cancelled = true; };
  }, [value, selected?.customer_id]);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setLoading(true);
    setError(null);
    const timer = window.setTimeout(() => {
      api.customers({ q: term.trim() || undefined, limit: 8 }).then(page => {
        if (!cancelled) { setResults(page.items); setActive(0); }
      }).catch(err => { if (!cancelled) setError(err); })
        .finally(() => { if (!cancelled) setLoading(false); });
    }, 200);
    return () => { cancelled = true; window.clearTimeout(timer); };
  }, [term, open]);

  const options: (CustomerIdentity | null)[] = term.trim() ? results : [null, ...results];
  function choose(customer: CustomerIdentity | null) {
    setSelected(customer); setTerm(""); setOpen(false); setError(null);
    onChange(customer?.customer_id ?? "");
  }
  return (
    <div className="grid-filter customer-filter">
      <span>Customer</span>
      <div className="customer-picker">
        <div className="picker-input">
          <Icon name="search" size={16} />
          <input role="combobox" aria-label="Customer search" aria-expanded={open} aria-controls={listId}
            aria-autocomplete="list" aria-activedescendant={open && options.length ? `${listId}-${active}` : undefined}
            placeholder={open ? "Name, contact number or customer code…" : "All customers"}
            value={open ? term : value ? selected?.customer_id === value ? selected.display_name || value : value : ""}
            onFocus={() => { setTerm(""); setOpen(true); }}
            onBlur={() => window.setTimeout(() => setOpen(false), 150)}
            onChange={event => { setTerm(event.target.value); setResults([]); setActive(0); setOpen(true); }}
            onKeyDown={event => {
              if (event.key === "ArrowDown" || event.key === "ArrowUp") {
                event.preventDefault();
                setActive(i => Math.max(0, Math.min(options.length - 1, i + (event.key === "ArrowDown" ? 1 : -1))));
              } else if (event.key === "Enter" && open) {
                event.preventDefault();
                if (!loading && options.length) choose(options[active]);
              } else if (event.key === "Escape") { event.preventDefault(); setOpen(false); }
            }} />
          {value && <button type="button" className="icon-button" aria-label="Delete the client" onClick={() => choose(null)}>
            <Icon name="close" size={14} />
          </button>}
        </div>
        {open && <ul className="picker-list" id={listId} role="listbox" aria-label="Customers">
          {loading ? <li className="picker-empty">Searching…</li> : options.map((customer, index) => <li
            id={`${listId}-${index}`} key={customer?.customer_id ?? "all"} role="option" aria-selected={index === active}
            className={index === active ? "active" : ""} onMouseDown={event => { event.preventDefault(); choose(customer); }}>
            <strong>{customer?.display_name || customer?.customer_id || "All customers"}</strong>
            {customer && <small>{customer.customer_id}{customer.phone ? ` · ${customer.phone}` : ""}</small>}
          </li>)}
          {!loading && !options.length && <li className="picker-empty">No customer found.</li>}
        </ul>}
      </div>
      <ErrorBox error={error} />
    </div>
  );
}
