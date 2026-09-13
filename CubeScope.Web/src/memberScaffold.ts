// Generates the MDX skeleton of a calculated member (WITH MEMBER for the query,
// CREATE MEMBER for the MDX Script). Pure function, no Vue dependency — testable
// in isolation and reused by the dialog component (see MemberScaffoldDialog.vue).

export type MemberScaffoldType = 'with' | 'create'

export interface MemberScaffoldOptions {
  name: string
  type: MemberScaffoldType
  formatString?: string
  displayFolder?: string
}

/** Escapes a name as a bracketed MDX identifier (doubles the inner `]`). */
export function bracketIdentifier(name: string): string {
  return `[${name.replace(/]/g, ']]')}]`
}

/**
 * Builds the MDX skeleton. Returns an empty string if the name is empty/blank
 * (caller-side guard: "Insert" button disabled while the name is empty).
 */
export function generateMemberScaffold(opts: MemberScaffoldOptions): string {
  const name = opts.name.trim()
  if (!name) return ''

  const member = `[Measures].${bracketIdentifier(name)}`
  const fmt = opts.formatString?.trim() ?? ''
  const folder = opts.displayFolder?.trim() ?? ''

  if (opts.type === 'with') {
    const fmtSuffix = fmt ? `, FORMAT_STRING = "${fmt}"` : ''
    return `MEMBER ${member} AS\n    /* expression */${fmtSuffix}\n`
  }

  const lines = [
    `CREATE MEMBER CURRENTCUBE.${member} AS`,
    `    /* expression */,`,
    `FORMAT_STRING = "${fmt || '#,##0.00'}",`,
  ]
  if (folder) lines.push(`DISPLAY_FOLDER = '${folder}',`)
  lines.push(`VISIBLE = 1;`)
  return lines.join('\n') + '\n'
}
