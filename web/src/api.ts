import type { Job, JobPage, JobTree } from './types';

const baseUrl = (import.meta.env.VITE_API_URL as string | undefined) ?? 'http://localhost:5129';

/** An API error carrying the ProblemDetails title and any per-field validation messages. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly fieldErrors: Record<string, string[]> = {},
  ) {
    super(message);
  }
}

interface ProblemDetails {
  title?: string;
  detail?: string;
  errors?: Record<string, string[]>;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${baseUrl}${path}`, {
      ...init,
      headers: { 'Content-Type': 'application/json', ...init?.headers },
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error;
    throw new ApiError(`Cannot reach the API at ${baseUrl}.`, 0);
  }

  if (!response.ok) {
    throw await toApiError(response);
  }
  return (await response.json()) as T;
}

async function toApiError(response: Response): Promise<ApiError> {
  const problem = (await response.json().catch(() => ({}))) as ProblemDetails;
  const message = problem.detail ?? problem.title ?? `Request failed (${response.status}).`;
  return new ApiError(message, response.status, problem.errors);
}

export function createJob(url: string, maxDepth?: number): Promise<{ jobId: string }> {
  return request('/api/jobs', { method: 'POST', body: JSON.stringify({ url, maxDepth }) });
}

export function getJob(id: string, signal?: AbortSignal): Promise<Job> {
  return request(`/api/jobs/${encodeURIComponent(id)}`, { signal });
}

export function getJobTree(id: string, signal?: AbortSignal): Promise<JobTree> {
  return request(`/api/jobs/${encodeURIComponent(id)}/tree`, { signal });
}

export function listJobs(page: number, pageSize: number, signal?: AbortSignal): Promise<JobPage> {
  return request(`/api/jobs?page=${page}&pageSize=${pageSize}`, { signal });
}
