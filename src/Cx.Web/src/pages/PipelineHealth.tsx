import GridPanel from "../components/GridPanel";
import DataTable from "../components/DataTable";
import { api } from '../api';
import { Empty, ErrorBox, Loading, PageHead, useAsync } from '../components/Common';
import { fmtDateTime, fmtNumber } from '../format';

export default function PipelineHealth() {
  const health = useAsync(() => api.pipelines(), []);
  const integrations = useAsync(() => api.integrations(), []);

  const data = health.data;

  return (
    <>
      <PageHead
        title="Connections and processing"
        actions={
          <button
            onClick={() => {
              health.reload();
              integrations.reload();
            }}
          >
            Update
          </button>
        }
      />

      <ErrorBox error={health.error} />
      {health.loading && <Loading />}

      {data && (
        <>
          <div className="grid cols-4" style={{ marginBottom: 14 }}>
            <div className="card kpi">
              <div className="label">queue outbox</div>
              <div
                className="value"
                style={{ color: data.outbox_backlog > 100 ? 'var(--danger)' : undefined }}
              >
                {fmtNumber(data.outbox_backlog)}
              </div>
              <div className="hint">
                Delay:{' '}
                {data.outbox_lag_seconds === null || data.outbox_lag_seconds === undefined
                  ? '—'
                  : `${Math.round(data.outbox_lag_seconds)} Sec`}
              </div>
            </div>
            <div className="card kpi">
              <div className="label">Last 1 hour event</div>
              <div className="value">{fmtNumber(data.events_last_hour)}</div>
              <div className="hint">24 hours: {fmtNumber(data.events_last_24h)}</div>
            </div>
            <div className="card kpi">
              <div className="label">Analysis AI (24 hours)</div>
              <div className="value">{fmtNumber(data.ai_analyses_last_24h)}</div>
              <div className="hint">Transcription: {fmtNumber(data.transcripts_last_24h)}</div>
            </div>
          </div>

          <div className="grid cols-2" style={{ marginBottom: 14 }}>
            <GridPanel>
              <h3 className="section-title">Status jobitems</h3>
              <StateTable states={data.jobs_by_state} />
            </GridPanel>
            <GridPanel>
              <h3 className="section-title">Status of audio files</h3>
              <StateTable states={data.recordings_by_state} />
            </GridPanel>
          </div>

          <GridPanel style={{ marginBottom: 14 }}>
            <h3 className="section-title">jobUnsuccessful</h3>
            {data.failed_jobs.length === 0 ? (
              <Empty text="job There is no failure" />
            ) : (
              <DataTable>
                <thead>
                  <tr>
                    <th>job</th>
                    <th>Type</th>
                    <th>Status</th>
                    <th>Effort</th>
                    <th>Error category</th>
                    <th>Error</th>
                    <th>Time</th>
                  </tr>
                </thead>
                <tbody>
                  {data.failed_jobs.map((job) => (
                    <tr key={job.job_key}>
                      <td className="mono small" style={{ maxWidth: 260, wordBreak: 'break-all' }}>
                        {job.job_key}
                      </td>
                      <td>{job.job_type}</td>
                      <td>
                        <span className={`badge ${job.state === 'FAILED_FINAL' ? 'negative' : 'warn'}`}>
                          {job.state}
                        </span>
                      </td>
                      <td>{job.attempts}</td>
                      <td className="small">{job.error_category ?? '—'}</td>
                      <td className="small muted" style={{ maxWidth: 320 }}>
                        {job.last_error?.slice(0, 160) ?? '—'}
                      </td>
                      <td className="small muted">{fmtDateTime(job.updated_at)}</td>
                    </tr>
                  ))}
                </tbody>
              </DataTable>
            )}
          </GridPanel>
        </>
      )}

      <GridPanel>
        <h3 className="section-title">Integrations</h3>
        <ErrorBox error={integrations.error} />
        {integrations.data && (
          <>
            <DataTable>
              <thead>
                <tr>
                  <th>Service</th>
                  <th>Type</th>
                  <th>Status</th>
                  <th>Details</th>
                </tr>
              </thead>
              <tbody>
                {integrations.data.live.map((item) => (
                  <tr key={`${item.type}-${item.name}`}>
                    <td className="mono">{item.name}</td>
                    <td>{item.type}</td>
                    <td>
                      <span className={`badge ${item.healthy ? 'ok' : 'warn'}`}>
                        {item.healthy ? 'Healthy' : 'Disabled'}
                      </span>
                    </td>
                    <td className="small muted">{item.detail ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </DataTable>

            {integrations.data.checkpoints.length > 0 && (
              <DataTable style={{ marginTop: 14 }}>
                <thead>
                  <tr>
                    <th>checkpoint</th>
                    <th>Type</th>
                    <th>Status</th>
                    <th>The last success</th>
                    <th>Records</th>
                  </tr>
                </thead>
                <tbody>
                  {integrations.data.checkpoints.map((cp) => (
                    <tr key={cp.integration_name}>
                      <td className="mono">{cp.integration_name}</td>
                      <td>{cp.integration_type}</td>
                      <td>
                        <span className={`badge ${cp.status === 'healthy' ? 'ok' : 'negative'}`}>
                          {cp.status}
                        </span>
                      </td>
                      <td className="small muted">{fmtDateTime(cp.last_success_at)}</td>
                      <td>{fmtNumber(cp.records_processed)}</td>
                    </tr>
                  ))}
                </tbody>
              </DataTable>
            )}
          </>
        )}
      </GridPanel>
    </>
  );
}

function StateTable({ states }: { states: Record<string, number> }) {
  const entries = Object.entries(states);
  if (entries.length === 0) return <Empty text="An unregistered case" />;

  return (
    <DataTable>
      <thead><tr><th>Status</th><th>Count</th></tr></thead>
      <tbody>
        {entries.map(([state, count]) => (
          <tr key={state}>
            <td>
              <span className="badge">{state}</span>
            </td>
            <td style={{ textAlign: 'end' }}>{fmtNumber(count)}</td>
          </tr>
        ))}
      </tbody>
    </DataTable>
  );
}
