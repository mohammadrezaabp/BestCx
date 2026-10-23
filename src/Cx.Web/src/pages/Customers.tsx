import GridPanel, { GridFilters } from "../components/GridPanel";
import DataTable from "../components/DataTable";
import { useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { api } from "../api";
import { Empty, ErrorBox, Loading, PageHead, Pager, useAsync, useCursorPaging } from "../components/Common";
import CustomerForm from "../components/CustomerForm";
import Icon from "../components/Icon";
import Modal from "../components/Modal";
import { fmtDateTime, fmtNumber } from "../format";

function CustomerTable({ q }: { q: string }) {
  const paging = useCursorPaging();
  const customers = useAsync(
    () => api.customers({ q: q || undefined, cursor: paging.cursor, limit: paging.pageSize }),
    [q, paging.cursor, paging.pageSize],
  );
  return (
    <GridPanel>
      <ErrorBox error={customers.error} />
      {customers.loading && <Loading />}
      {customers.data?.items.length === 0 && (
        <Empty text={q ? "No customer was found with this specification." : "The customer has not yet been defined."} />
      )}
      {customers.data && customers.data.items.length > 0 && (
        <div className="table-scroll">
          <DataTable paginate={false} className="data-grid">
            <thead>
              <tr>
                <th>Customer</th>
                <th>Call</th>
                <th>Inc / Group</th>
                <th>Feedback</th>
                <th>Last interaction</th>
              </tr>
            </thead>
            <tbody>
              {customers.data.items.map((customer) => (
                <tr key={customer.customer_id}>
                  <td>
                    <Link
                      className="customer-cell"
                      to={`/customers/${encodeURIComponent(customer.customer_id)}`}
                    >
                      <span className="avatar">
                        {(customer.display_name || customer.customer_id).slice(0, 1)}
                      </span>
                      <span>
                        <strong>{customer.display_name || customer.customer_id}</strong>
                        <small className="mono">{customer.customer_id}</small>
                      </span>
                    </Link>
                  </td>
                  <td className="small">
                    {customer.phone && (
                      <div dir="ltr" className="mono">
                        {customer.phone}
                      </div>
                    )}
                    {customer.email && (
                      <div dir="ltr" className="muted">
                        {customer.email}
                      </div>
                    )}
                    {!customer.phone && !customer.email && <span className="muted">—</span>}
                  </td>
                  <td className="small">
                    {customer.business_id || <span className="muted">—</span>}
                    {customer.segment && (
                      <div>
                        <span className="badge">{customer.segment}</span>
                      </div>
                    )}
                  </td>
                  <td>
                    <strong>{fmtNumber(customer.feedback_count)}</strong>
                    {customer.negative_count > 0 && (
                      <span className="badge negative" style={{ marginInlineStart: 6 }}>
                        {fmtNumber(customer.negative_count)} Negative
                      </span>
                    )}
                  </td>
                  <td className="small muted nowrap">{fmtDateTime(customer.last_seen_at)}</td>
                </tr>
              ))}
            </tbody>
          </DataTable>
        </div>
      )}
      <Pager page={paging.page} pageSize={paging.pageSize} onPageSize={paging.setPageSize}
        hasMore={Boolean(customers.data?.has_more)}
        canGoBack={paging.canGoBack}
        onNext={() => paging.next(customers.data?.next_cursor)}
        onBack={paging.back}
      />
    </GridPanel>
  );
}

export default function Customers() {
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const [creating, setCreating] = useState(false);
  const q = params.get("q") ?? "";
  return (
    <>
      <PageHead
        title="Customers"
        actions={
          <button className="primary" onClick={() => setCreating(true)}>
            <Icon name="plus" size={16} /> New customer
          </button>
        }
      />
      <GridFilters as="form"
        key={`search-${q}`}

        role="search"
        onSubmit={(event) => {
          event.preventDefault();
          const term = String(new FormData(event.currentTarget).get("q") || "").trim();
          setParams(term ? { q: term } : {});
        }}
      >
        <label className="grid-filter"><span>Search in customers</span><div className="grid-search">
          <Icon name="search" size={16} />
          <input
            aria-label="Searching for customers"
            name="q"
            defaultValue={q}
            placeholder="Name, contact number, email, company or customer code…"
          />
        </div></label>
        <button className="primary" type="submit">
          Search
        </button>
        {q && (
          <>
            <button type="button" onClick={() => setParams({})}>
              Erase
            </button>
            <Link className="link-button" to={`/experience?q=${encodeURIComponent(q)}`}>
              Search «{q}» In feedback ←
            </Link>
          </>
        )}
      </GridFilters>
      <CustomerTable key={q} q={q} />
      {creating && (
        <Modal title="Definition of new customer" onClose={() => setCreating(false)} wide>
          <CustomerForm
            onCancel={() => setCreating(false)}
            onSaved={(customer) => {
              setCreating(false);
              navigate(`/customers/${encodeURIComponent(customer.customer_id)}`);
            }}
          />
        </Modal>
      )}
    </>
  );
}
