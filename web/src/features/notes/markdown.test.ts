import { describe, expect, it } from 'vitest';
import { wikiToMarkdown } from './markdown';

describe('wiki links', () => {
  it('become Markdown links to the note title', () => {
    expect(wikiToMarkdown('See [[Project Apollo]] and [[Budget#2026|the budget]].')).toBe(
      'See [Project Apollo](wiki:Project%20Apollo) and [the budget](wiki:Budget).',
    );
    expect(wikiToMarkdown('![[Diagram]]')).toBe('↳ [Diagram](wiki:Diagram)');
  });

  it('are left alone in code', () => {
    expect(wikiToMarkdown('`[[not a link]]` and\n```\n[[also not]]\n```')).toBe(
      '`[[not a link]]` and\n```\n[[also not]]\n```',
    );
  });
});
