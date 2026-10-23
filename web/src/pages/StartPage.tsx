import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { ApiError, createJob } from '../api';

const defaultMaxDepth = 2;

export function StartPage() {
  const navigate = useNavigate();
  const [url, setUrl] = useState('');
  const [maxDepth, setMaxDepth] = useState(defaultMaxDepth);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      const { jobId } = await createJob(url.trim(), Number.isFinite(maxDepth) ? maxDepth : undefined);
      navigate(`/jobs/${jobId}`);
    } catch (e) {
      setError(e instanceof ApiError ? e : new ApiError('Unexpected error.', 0));
      setSubmitting(false);
    }
  }

  const fieldError = (name: string) => error?.fieldErrors[name]?.join(' ');
  const hasFieldErrors = error !== null && Object.keys(error.fieldErrors).length > 0;

  return (
    <section>
      <h1>Start a crawl</h1>
      <form className="start-form" onSubmit={handleSubmit} noValidate>
        <label>
          Start URL
          <input
            type="url"
            required
            placeholder="https://example.com"
            value={url}
            onChange={(e) => setUrl(e.target.value)}
          />
          {fieldError('url') && <span className="field-error">{fieldError('url')}</span>}
        </label>
        <label>
          Max depth (0–5)
          <input
            type="number"
            min={0}
            max={5}
            value={maxDepth}
            onChange={(e) => setMaxDepth(e.target.valueAsNumber)}
          />
          {fieldError('maxDepth') && <span className="field-error">{fieldError('maxDepth')}</span>}
        </label>
        <button type="submit" disabled={submitting || url.trim() === ''}>
          {submitting ? 'Starting…' : 'Start Crawl'}
        </button>
        {error && !hasFieldErrors && <p className="error">{error.message}</p>}
      </form>
    </section>
  );
}
