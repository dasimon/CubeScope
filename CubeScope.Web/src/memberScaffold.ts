// Génération du squelette MDX d'un membre calculé (WITH MEMBER pour la requête,
// CREATE MEMBER pour le MDX Script). Fonction pure, sans dépendance Vue — testable
// isolément et réutilisée par le composant de dialogue (voir MemberScaffoldDialog.vue).

export type MemberScaffoldType = 'with' | 'create'

export interface MemberScaffoldOptions {
  name: string
  type: MemberScaffoldType
  formatString?: string
  displayFolder?: string
}

/** Échappe un nom en identifiant MDX entre crochets (double les `]` internes). */
export function bracketIdentifier(name: string): string {
  return `[${name.replace(/]/g, ']]')}]`
}

/**
 * Construit le squelette MDX. Retourne une chaîne vide si le nom est vide/blanc
 * (garde côté appelant : bouton « Insérer » désactivé tant que le nom est vide).
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
