import { useParams } from 'react-router-dom';
import { formatTime } from '../format';
import { PageTree } from '../components/PageTree';
import { StatusBadge } from '../components/StatusBadge';
import { useJobPolling } from '../useJobPolling';
import { isTerminal, type Job } from '../types';

export function JobPage() {
  const { id = '' } = useParams();
  const { job, tree, error } = useJobPolling(id);

  if (error?.status === 404) {
    return <p className="error">{error.message}</p>;
  }

  return (
    <section>
      <h1>Crawl job</h1>
      {error && <p className="error">{error.message} Retrying…</p>}
      {job === null ? <p className="muted">Loading…</p> : <JobSummary job={job} />}
      <h2>Pages</h2>
      {tree?.root ? <PageTree root={tree.root} /> : job && <p className="muted">No pages yet.</p>}
    </section>
  );
}

function JobSummary({ job }: { job: Job }) {
  return (
    <dl className="summary">
      <dt>URL</dt>
      <dd>{job.url}</dd>
      <dt>Status</dt>
      <dd>
        <StatusBadge status={job.status} />
        {!isTerminal(job.status) && <span className="muted"> refreshing…</span>}
      </dd>
      <dt>Progress</dt>
      <dd>
        {job.pagesProcessed} / {job.pagesEnqueued} pages processed (max {job.maxPages}, depth {job.maxDepth})
      </dd>
      {job.failureReason && (
        <>
          <dt>Failure</dt>
          <dd className="error">{job.failureReason}</dd>
        </>
      )}
      <dt>Created</dt>
      <dd>{formatTime(job.createdAt)}</dd>
      <dt>Started</dt>
      <dd>{formatTime(job.startedAt)}</dd>
      <dt>Finished</dt>
      <dd>{formatTime(job.completedAt)}</dd>
    </dl>
  );
}
