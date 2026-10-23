import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { TreeNode } from '../types';
import { PageTree } from './PageTree';

function node(url: string, overrides: Partial<TreeNode> = {}): TreeNode {
  return {
    url,
    depth: 0,
    status: 'Fetched',
    domainLinkRatio: null,
    error: null,
    outgoingLinks: [],
    children: [],
    ...overrides,
  };
}

describe('PageTree', () => {
  const tree = node('https://site.test/', {
    domainLinkRatio: 0.5,
    outgoingLinks: [
      { url: 'https://other.test/', isSameDomain: false },
      { url: 'https://site.test/a', isSameDomain: true },
    ],
    children: [
      node('https://site.test/a', {
        depth: 1,
        status: 'Failed',
        error: 'HTTP 500',
        children: [node('https://site.test/a/deep', { depth: 2, status: 'Skipped' })],
      }),
    ],
  });

  const html = renderToStaticMarkup(<PageTree root={tree} />);

  it('renders every page, nested to its depth', () => {
    const list = '<ul class="tree">';
    expect(html.split(list).length - 1).toBe(3);
    expect(html.indexOf('https://site.test/a/deep')).toBeGreaterThan(html.indexOf('>https://site.test/a<'));
  });

  it('shows the ratio, or a dash for a page with none', () => {
    expect(html).toContain('ratio 0.50');
    expect(html).toContain('ratio —');
  });

  it('summarizes internal and external links and shows page errors and statuses', () => {
    expect(html).toContain('2 outgoing links (1 internal, 1 external)');
    expect(html).toContain('HTTP 500');
    expect(html).toContain('badge-failed');
    expect(html).toContain('badge-skipped');
  });
});
