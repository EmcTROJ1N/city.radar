import { describe, expect, it } from 'vitest';
import type { GlobalSettingsPatch } from '@shared/api/types';
import { formFromSettings, settingsPatch } from './settings-patch';

const CURRENT: GlobalSettingsPatch = {
  observationRatePerHour: 20,
  commentMaxLength: 500,
  confidenceThresholdDefault: 0.7,
  anomalyWindowMinutes: 5,
  anomalyThreshold: 30,
  freshnessFreshRatio: 0.5,
  proxySecretId: null,
};

describe('settingsPatch', () => {
  it('отправляет только изменённые поля', () => {
    const form = { ...formFromSettings(CURRENT), commentMaxLength: 800, proxySecretId: 'proxy-1' };

    expect(settingsPatch(CURRENT, form)).toEqual({
      commentMaxLength: 800,
      proxySecretId: 'proxy-1',
    });
  });

  it('возвращает пустой патч, если ничего не изменилось', () => {
    expect(settingsPatch(CURRENT, formFromSettings(CURRENT))).toEqual({});
  });

  it('передаёт null, когда глобальный прокси снят', () => {
    const current = { ...CURRENT, proxySecretId: 'proxy-1' };
    const form = { ...formFromSettings(current), proxySecretId: '' };

    expect(settingsPatch(current, form)).toEqual({ proxySecretId: null });
  });
});
