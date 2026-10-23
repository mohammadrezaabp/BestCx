import { useState } from "react";
import { api } from "../api";
import { ErrorBox, Loading, useAsync } from "./Common";

export default function CustomerAccountForm({ customerId, onSaved, onCancel }: {
  customerId: string; onSaved: () => void; onCancel: () => void;
}) {
  const account = useAsync(() => api.customerAccount(customerId), [customerId]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    setBusy(true);
    setError(null);
    try {
      await api.saveCustomerAccount(customerId, {
        username: String(form.get("username") ?? "").trim(),
        password: String(form.get("password") ?? "") || undefined,
        enabled: form.get("enabled") === "on",
      });
      onSaved();
    } catch (err) { setError(err); }
    finally { setBusy(false); }
  }

  if (account.loading) return <Loading />;
  if (!account.data) return <ErrorBox error={account.error} />;
  return (
    <form onSubmit={submit} className="customer-form">
      <ErrorBox error={error} />
      <fieldset disabled={busy}>
        <label className="field">
          <span>Username</span>
          <input name="username" required maxLength={128} dir="ltr" autoComplete="off"
            defaultValue={account.data.username ?? customerId} />
        </label>
        <label className="field">
          <span>{account.data.exists ? "New password (Optional)" : "Password"}</span>
          <input name="password" type="password" required={!account.data.exists}
            minLength={8} maxLength={256} dir="ltr" autoComplete="new-password" />
        </label>
        {account.data.exists && <p className="small muted">Leave this field blank to keep the current password.</p>}
        <label><input name="enabled" type="checkbox" defaultChecked={account.data.enabled} /> The account is active</label>
        <div className="form-submit">
          <button className="primary" type="submit">{busy ? "Saving…" : "Save customer account"}</button>
          <button type="button" onClick={onCancel}>Cancel</button>
        </div>
      </fieldset>
    </form>
  );
}
