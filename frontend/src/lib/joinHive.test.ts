import { describe, expect, it } from 'vitest'
import { joinQuestion } from './joinHive'

const texts = { confirm: 'Wi-Fi {ssid}, hive {url}?', replaces: 'Replaces {name} ({id}): its old board loses access.', byCode: 'The code decides.' }

describe('joining the hive in place of a hornet', () => {
  it('names the hornet whose old board loses access', () => {
    expect(joinQuestion(texts, 'suburban-IoT', 'http://192.168.10.4', { name: 'Машинка', deviceId: 'car-cam' }))
      .toBe('Wi-Fi suburban-IoT, hive http://192.168.10.4?\n\nReplaces Машинка (car-cam): its old board loses access.')
  })

  it('says the code decides when no hornet is picked', () => {
    expect(joinQuestion(texts, 'x', 'http://h', null)).toBe('Wi-Fi x, hive http://h?\n\nThe code decides.')
  })
})
