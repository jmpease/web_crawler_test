import type { JobStatus, PageStatus } from '../types';

export function StatusBadge({ status }: { status: JobStatus | PageStatus }) {
  return <span className={`badge badge-${status.toLowerCase()}`}>{status}</span>;
}
