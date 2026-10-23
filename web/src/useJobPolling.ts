import { useEffect, useState } from 'react';
import { ApiError, getJob, getJobTree } from './api';
import { isTerminal, type Job, type JobTree } from './types';

export const pollIntervalMs = 1500;

export interface JobPollState {
  job: Job | null;
  tree: JobTree | null;
  error: ApiError | null;
}

/**
 * Polls the job every 1.5 s until it reaches a terminal status. The tree is refetched only when
 * the processed-page count changes, plus once at the end. A failed poll is shown and retried on
 * the next tick; a 404 stops polling.
 */
export function useJobPolling(jobId: string): JobPollState {
  const [state, setState] = useState<JobPollState>({ job: null, tree: null, error: null });

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let lastProcessed = -1;
    let lastStatus = '';

    setState({ job: null, tree: null, error: null });

    async function poll() {
      try {
        const job = await getJob(jobId, controller.signal);
        const treeIsStale = job.pagesProcessed !== lastProcessed || job.status !== lastStatus;
        const tree = treeIsStale ? await getJobTree(jobId, controller.signal) : undefined;
        lastProcessed = job.pagesProcessed;
        lastStatus = job.status;
        setState((prev) => ({ job, tree: tree ?? prev.tree, error: null }));
        if (!isTerminal(job.status)) schedule();
      } catch (e) {
        if (controller.signal.aborted) return;
        const error = e instanceof ApiError ? e : new ApiError('Unexpected error.', 0);
        setState((prev) => ({ ...prev, error }));
        if (error.status !== 404) schedule();
      }
    }

    function schedule() {
      timer = setTimeout(poll, pollIntervalMs);
    }

    void poll();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [jobId]);

  return state;
}
