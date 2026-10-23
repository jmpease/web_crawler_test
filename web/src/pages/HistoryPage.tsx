import { useEffect, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { ApiError, listJobs } from '../api';
import { StatusBadge } from '../components/StatusBadge';
import type { JobPage } from '../types';
import { formatTime } from '../format';

const pageSize = 20;

export function HistoryPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const page = Math.max(1, Number(searchParams.get('page')) || 1);
  const navigate = useNavigate();
  const [data, setData] = useState<JobPage | null>(null);
  const [error, setError] = useState<ApiError | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setError(null);
    listJobs(page, pageSize, controller.signal)
      .then(setData)
      .catch((e) => {
        if (!controller.signal.aborted) setError(e instanceof ApiError ? e : new ApiError('Unexpected error.', 0));
      });
    return () => controller.abort();
  }, [page]);

  const pageCount = data ? Math.max(1, Math.ceil(data.total / pageSize)) : 1;
  const goTo = (p: number) => setSearchParams({ page: String(p) });

  return (
    <section>
      <h1>History</h1>
      {error && <p className="error">{error.message}</p>}
      {data === null && !error && <p className="muted">Loading…</p>}
      {data && data.items.length === 0 && (
        <p className="muted">
          No crawls yet. <Link to="/">Start one.</Link>
        </p>
      )}
      {data && data.items.length > 0 && (
        <>
          <table className="history">
            <thead>
              <tr>
                <th>Created</th>
                <th>URL</th>
                <th>Status</th>
                <th>Pages</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((job) => (
                <tr key={job.id} onClick={() => navigate(`/jobs/${job.id}`)}>
                  <td>{formatTime(job.createdAt)}</td>
                  <td>
                    <Link to={`/jobs/${job.id}`}>{job.url}</Link>
                  </td>
                  <td>
                    <StatusBadge status={job.status} />
                  </td>
                  <td>
                    {job.pagesProcessed} / {job.pagesEnqueued}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="pager">
            <button onClick={() => goTo(page - 1)} disabled={page <= 1}>
              Previous
            </button>
            <span>
              Page {page} of {pageCount} ({data.total} jobs)
            </span>
            <button onClick={() => goTo(page + 1)} disabled={page >= pageCount}>
              Next
            </button>
          </div>
        </>
      )}
    </section>
  );
}
