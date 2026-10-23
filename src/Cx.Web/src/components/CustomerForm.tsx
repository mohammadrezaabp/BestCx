import { useState } from "react";
import { api, type CustomerIdentity, type CustomerInput } from "../api";
import { ErrorBox } from "./Common";
import Icon from "./Icon";

const segments = ["Normal", "Special", "Organizational", "New", "subject to shedding"];

/** Defines a new customer, or edits one when `customer` is given. */
export default function CustomerForm({
  customer,
  onSaved,
  onCancel,
}: {
  customer?: CustomerIdentity;
  onSaved: (customer: CustomerIdentity) => void;
  onCancel: () => void;
}) {
  const editing = Boolean(customer);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    // React bubbles events along the component tree, portals included: without this the
    // feedback form that opened this dialog would submit too.
    event.stopPropagation();
    const form = new FormData(event.currentTarget);
    const text = (name: string) => String(form.get(name) ?? "").trim() || undefined;
    const body: CustomerInput = {
      customer_id: editing ? undefined : text("customer_id"),
      display_name: text("display_name") ?? "",
      phone: text("phone"),
      email: text("email"),
      business_id: text("business_id"),
      segment: text("segment"),
      notes: text("notes"),
    };
    if (!body.display_name) {
      setError(new Error("Enter the customer name."));
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const saved = editing
        ? await api.updateCustomer(customer!.customer_id, body)
        : await api.createCustomer(body);
      onSaved(saved);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="customer-form">
      <ErrorBox error={error} />
      <fieldset disabled={busy}>
        <div className="form-grid">
          <label className="field">
            <span>
              Name and surname <span className="required">*</span>
            </span>
            <input
              name="display_name"
              required
              maxLength={256}
              defaultValue={customer?.display_name ?? ""}
              autoFocus
            />
          </label>
          <label className="field">
            <span>Phone number</span>
            <input
              name="phone"
              inputMode="tel"
              dir="ltr"
              maxLength={30}
              placeholder="0912…"
              defaultValue={customer?.phone ?? ""}
            />
          </label>
        </div>
        <div className="form-grid">
          <label className="field">
            <span>Email</span>
            <input
              name="email"
              type="email"
              dir="ltr"
              maxLength={200}
              defaultValue={customer?.email ?? ""}
            />
          </label>
          <label className="field">
            <span>Inc / Business</span>
            <input
              name="business_id"
              maxLength={128}
              defaultValue={customer?.business_id ?? ""}
            />
          </label>
        </div>
        <div className="form-grid">
          <label className="field">
            <span>Customer group</span>
            <input
              name="segment"
              list="customer-segments"
              maxLength={64}
              placeholder="For example, special"
              defaultValue={customer?.segment ?? ""}
            />
            <datalist id="customer-segments">
              {segments.map((segment) => (
                <option key={segment} value={segment} />
              ))}
            </datalist>
          </label>
          {!editing && (
            <label className="field">
              <span>
                Customer code <span className="muted">(Optional)</span>
              </span>
              <input
                name="customer_id"
                dir="ltr"
                maxLength={128}
                pattern="[A-Za-z0-9._@\-]+"
                title="English letters, numbers and . _ - @"
                placeholder="It is made automatically"
              />
            </label>
          )}
        </div>
        <label className="field">
          <span>Note</span>
          <textarea
            name="notes"
            rows={3}
            maxLength={2000}
            defaultValue={customer?.notes ?? ""}
          />
        </label>
        <div className="form-submit">
          <button className="primary" type="submit">
            <Icon name="check" size={16} />
            {busy ? "Saving…" : editing ? "Save changes" : "Definition of customer"}
          </button>
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
        </div>
      </fieldset>
    </form>
  );
}
