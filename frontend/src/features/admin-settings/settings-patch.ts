import type { GlobalSettingsPatch } from '@shared/api/types';

export interface SettingsForm {
  observationRatePerHour: number;
  commentMaxLength: number;
  confidenceThresholdDefault: number;
  anomalyWindowMinutes: number;
  anomalyThreshold: number;
  freshnessFreshRatio: number;
  proxySecretId: string;
}

export const SETTINGS_DEFAULTS: SettingsForm = {
  observationRatePerHour: 20,
  commentMaxLength: 500,
  confidenceThresholdDefault: 0.7,
  anomalyWindowMinutes: 5,
  anomalyThreshold: 30,
  freshnessFreshRatio: 0.5,
  proxySecretId: '',
};

export function formFromSettings(settings: GlobalSettingsPatch): SettingsForm {
  return {
    observationRatePerHour: settings.observationRatePerHour ?? SETTINGS_DEFAULTS.observationRatePerHour,
    commentMaxLength: settings.commentMaxLength ?? SETTINGS_DEFAULTS.commentMaxLength,
    confidenceThresholdDefault:
      settings.confidenceThresholdDefault ?? SETTINGS_DEFAULTS.confidenceThresholdDefault,
    anomalyWindowMinutes: settings.anomalyWindowMinutes ?? SETTINGS_DEFAULTS.anomalyWindowMinutes,
    anomalyThreshold: settings.anomalyThreshold ?? SETTINGS_DEFAULTS.anomalyThreshold,
    freshnessFreshRatio: settings.freshnessFreshRatio ?? SETTINGS_DEFAULTS.freshnessFreshRatio,
    proxySecretId: settings.proxySecretId ?? '',
  };
}

/** Частичное обновление: только поля, отличающиеся от текущих настроек. */
export function settingsPatch(current: GlobalSettingsPatch, form: SettingsForm): GlobalSettingsPatch {
  const patch: GlobalSettingsPatch = {};
  if (form.observationRatePerHour !== current.observationRatePerHour) {
    patch.observationRatePerHour = form.observationRatePerHour;
  }
  if (form.commentMaxLength !== current.commentMaxLength) {
    patch.commentMaxLength = form.commentMaxLength;
  }
  if (form.confidenceThresholdDefault !== current.confidenceThresholdDefault) {
    patch.confidenceThresholdDefault = form.confidenceThresholdDefault;
  }
  if (form.anomalyWindowMinutes !== current.anomalyWindowMinutes) {
    patch.anomalyWindowMinutes = form.anomalyWindowMinutes;
  }
  if (form.anomalyThreshold !== current.anomalyThreshold) {
    patch.anomalyThreshold = form.anomalyThreshold;
  }
  if (form.freshnessFreshRatio !== current.freshnessFreshRatio) {
    patch.freshnessFreshRatio = form.freshnessFreshRatio;
  }
  if (form.proxySecretId !== (current.proxySecretId ?? '')) {
    patch.proxySecretId = form.proxySecretId || null;
  }
  return patch;
}
