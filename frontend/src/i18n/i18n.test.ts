import { describe, expect, it } from 'vitest'
import { t } from '@/i18n'

describe('t', () => {
  it('finds a leaf key that itself contains dots', () => {
    expect(t('secrets.names.telegram.token')).not.toBe('secrets.names.telegram.token')
    expect(t('secrets.names.assistant.apikey')).toBe('Ключ LLM для ассистента')
  })
  it('still resolves nested keys and falls back to the key', () => {
    expect(t('secrets.save')).toBe('Сохранить')
    expect(t('no.such.key')).toBe('no.such.key')
    expect(t('no.such.key', 'fb')).toBe('fb')
  })
})
