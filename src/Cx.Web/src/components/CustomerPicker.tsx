import { useEffect, useId, useState } from "react";
import { api, type CustomerIdentity } from "../api";
import CustomerForm from "./CustomerForm";
import Icon from "./Icon";
import Modal from "./Modal";

/**
 * Pick the customer a feedback belongs to: type to search by name, phone or code, or
 * define a new customer without leaving the form.
 */
export default function CustomerPicker({
  value,
  onChange,
  disabled,
}: {
  value: CustomerIdentity | null;
  onChange: (customer: CustomerIdentity | null) => void;
  disabled?: boolean;
}) {
  const [term, setTerm] = useState("");
  const [results, setResults] = useState<CustomerIdentity[]>([]);
  const [open, setOpen] = useState(false);
  const [creating, setCreating] = useState(false);
  const [active, setActive] = useState(0);
  const listId = useId();

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    const timer = window.setTimeout(() => {
      api
        .customers({ q: term.trim() || undefined, limit: 8 })
        .then((page) => {
          if (cancelled) return;
          setResults(page.items);
          setActive(0);
        })
        .catch(() => {
          if (!cancelled) setResults([]);
        });
    }, 200);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [term, open]);

  function choose(customer: CustomerIdentity) {
    onChange(customer);
    setOpen(false);
    setTerm("");
  }

  if (value)
    return (
      <div className="picked-customer">
        <span className="avatar">{(value.display_name || value.customer_id).slice(0, 1)}</span>
        <div>
          <strong>{value.display_name || value.customer_id}</strong>
          <small>
            {value.customer_id}
            {value.phone ? ` · ${value.phone}` : ""}
          </small>
        </div>
        <button type="button" onClick={() => onChange(null)} disabled={disabled}>
          Change
        </button>
      </div>
    );

  return (
    <div className="customer-picker">
      <div className="picker-input">
        <Icon name="search" size={16} />
        <input
          role="combobox"
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-label="Customer search"
          placeholder="Name, contact number or customer code…"
          value={term}
          disabled={disabled}
          onFocus={() => setOpen(true)}
          onBlur={() => window.setTimeout(() => setOpen(false), 150)}
          onChange={(event) => {
            setTerm(event.target.value);
            setOpen(true);
          }}
          onKeyDown={(event) => {
            if (event.key === "ArrowDown") {
              event.preventDefault();
              setActive((i) => Math.min(i + 1, results.length - 1));
            } else if (event.key === "ArrowUp") {
              event.preventDefault();
              setActive((i) => Math.max(i - 1, 0));
            } else if (event.key === "Enter" && open && results[active]) {
              event.preventDefault();
              choose(results[active]);
            }
          }}
        />
        <button
          type="button"
          className="picker-new"
          onClick={() => setCreating(true)}
          disabled={disabled}
        >
          <Icon name="plus" size={15} /> New customer
        </button>
      </div>
      {open && (
        <ul className="picker-list" id={listId} role="listbox">
          {results.length === 0 && (
            <li className="picker-empty">
              No customer found.{" "}
              <button type="button" onMouseDown={() => setCreating(true)}>
                Definition of new customer
              </button>
            </li>
          )}
          {results.map((customer, index) => (
            <li
              key={customer.customer_id}
              role="option"
              aria-selected={index === active}
              className={index === active ? "active" : ""}
              onMouseDown={(event) => {
                event.preventDefault();
                choose(customer);
              }}
            >
              <strong>{customer.display_name || customer.customer_id}</strong>
              <small>
                {customer.customer_id}
                {customer.phone ? ` · ${customer.phone}` : ""}
                {customer.business_id ? ` · ${customer.business_id}` : ""}
              </small>
            </li>
          ))}
        </ul>
      )}
      {creating && (
        <Modal title="Definition of new customer" onClose={() => setCreating(false)} wide>
          <CustomerForm
            onCancel={() => setCreating(false)}
            onSaved={(customer) => {
              setCreating(false);
              choose(customer);
            }}
          />
        </Modal>
      )}
    </div>
  );
}
