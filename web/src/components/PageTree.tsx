import { formatRatio } from '../format';
import type { TreeNode } from '../types';
import { StatusBadge } from './StatusBadge';

export function PageTree({ root }: { root: TreeNode }) {
  return (
    <ul className="tree">
      <PageTreeNode node={root} />
    </ul>
  );
}

function PageTreeNode({ node }: { node: TreeNode }) {
  const internal = node.outgoingLinks.filter((l) => l.isSameDomain).length;

  return (
    <li>
      <div className="node">
        <StatusBadge status={node.status} />
        <a href={node.url} target="_blank" rel="noreferrer" className="node-url">
          {node.url}
        </a>
        <span className="ratio" title="Domain link ratio: same-domain links / all links">
          ratio {formatRatio(node.domainLinkRatio)}
        </span>
        <span className="muted">depth {node.depth}</span>
      </div>
      {node.error && <div className="node-error">{node.error}</div>}
      {node.outgoingLinks.length > 0 && (
        <details className="links">
          <summary>
            {node.outgoingLinks.length} outgoing links ({internal} internal,{' '}
            {node.outgoingLinks.length - internal} external)
          </summary>
          <ul>
            {node.outgoingLinks.map((link) => (
              <li key={link.url}>
                <span className={link.isSameDomain ? 'tag tag-internal' : 'tag tag-external'}>
                  {link.isSameDomain ? 'internal' : 'external'}
                </span>{' '}
                {link.url}
              </li>
            ))}
          </ul>
        </details>
      )}
      {node.children.length > 0 && (
        <ul className="tree">
          {node.children.map((child) => (
            <PageTreeNode key={child.url} node={child} />
          ))}
        </ul>
      )}
    </li>
  );
}
