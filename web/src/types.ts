// Mirrors the API's JSON (enums are serialized as strings).

export type JobStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Canceled';
export type PageStatus = 'Pending' | 'Fetched' | 'Skipped' | 'Failed';

export interface Job {
  id: string;
  url: string;
  maxDepth: number;
  maxPages: number;
  status: JobStatus;
  failureReason: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  pagesEnqueued: number;
  pagesProcessed: number;
  maxPagesReached: boolean;
  /** Present on a single job (GET /api/jobs/{id}), null in history rows. */
  pageCounts: PageCounts | null;
}

export interface PageCounts {
  pending: number;
  fetched: number;
  skipped: number;
  failed: number;
}

export interface JobPage {
  items: Job[];
  page: number;
  pageSize: number;
  total: number;
}

export interface OutgoingLink {
  url: string;
  isSameDomain: boolean;
}

export interface TreeNode {
  url: string;
  depth: number;
  status: PageStatus;
  domainLinkRatio: number | null;
  error: string | null;
  outgoingLinks: OutgoingLink[];
  children: TreeNode[];
}

export interface JobTree {
  jobId: string;
  status: JobStatus;
  root: TreeNode | null;
}

export const terminalStatuses: readonly JobStatus[] = ['Completed', 'Failed', 'Canceled'];

export function isTerminal(status: JobStatus): boolean {
  return terminalStatuses.includes(status);
}
