// Export des résultats de requête : CSV (téléchargement) et TSV (presse-papiers, colle propre dans Excel).
import type { GridColumn } from './api'

function csvField(value: unknown): string {
  const s = value === null || value === undefined ? '' : String(value)
  if (s.includes('"') || s.includes(',') || s.includes('\n') || s.includes('\r')) {
    return `"${s.replace(/"/g, '""')}"`
  }
  return s
}

export function toCsv(columns: GridColumn[], rows: Record<string, unknown>[]): string {
  const header = columns.map((c) => csvField(c.header)).join(',')
  const lines = rows.map((row) => columns.map((c) => csvField(row[c.field])).join(','))
  return [header, ...lines].join('\r\n')
}

function tsvField(value: unknown): string {
  const s = value === null || value === undefined ? '' : String(value)
  return s.replace(/[\t\r\n]/g, ' ')
}

export function toTsv(columns: GridColumn[], rows: Record<string, unknown>[]): string {
  const header = columns.map((c) => tsvField(c.header)).join('\t')
  const lines = rows.map((row) => columns.map((c) => tsvField(row[c.field])).join('\t'))
  return [header, ...lines].join('\r\n')
}

export function downloadCsv(filename: string, csv: string): void {
  const blob = new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8;' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  a.click()
  URL.revokeObjectURL(url)
}

export function copyToClipboard(text: string): Promise<void> {
  return navigator.clipboard.writeText(text)
}
