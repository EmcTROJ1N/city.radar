import type { components, paths } from './schema';

export type Schemas = components['schemas'];
export type ApiPaths = paths;

export type Me = Schemas['Me'];
export type Problem = Schemas['Problem'];
export type ValidationProblem = Schemas['ValidationProblem'];
export type TokenResponse = Schemas['TokenResponse'];

export type MapFeature = Schemas['MapFeature'];
export type MapFeaturesResponse = Schemas['MapFeaturesResponse'];
export type MapObjectCard = Schemas['MapObjectCard'];
export type SourceCardView = Schemas['SourceCardView'];
export type SourceStatusPublic = Schemas['SourceStatusPublic'];
export type PublicObservation = Schemas['PublicObservation'];
export type ObservationPage = Schemas['ObservationPage'];
export type ZonePointsResponse = Schemas['ZonePointsResponse'];
export type ZonePoint = Schemas['ZonePoint'];
export type StatusView = Schemas['StatusView'];
export type StatusColor = Schemas['StatusColor'];
export type Freshness = Schemas['Freshness'];
export type CoverageKind = Schemas['CoverageKind'];
export type Geometry = Schemas['Geometry'];
export type CreateObservationRequest = Schemas['CreateObservationRequest'];
export type CreateObservationResponse = Schemas['CreateObservationResponse'];

export type Source = Schemas['Source'];
export type SourceCreate = Schemas['SourceCreate'];
export type SourceUpdate = Schemas['SourceUpdate'];
export type SourcePage = Schemas['SourcePage'];
export type SourceHealth = Schemas['SourceHealth'];
export type SourceStatus = Schemas['SourceStatus'];
export type SourceStatusInput = Schemas['SourceStatusInput'];
export type SourceType = Schemas['SourceType'];
export type TelegramMode = Schemas['TelegramMode'];
export type SourceState = Schemas['SourceState'];
export type ObservationSourceType = Schemas['ObservationSourceType'];
export type SourceConnection = Schemas['SourceConnection'];

export type Category = Schemas['Category'];
export type CategoryInput = Schemas['CategoryInput'];
export type ObjectType = Schemas['ObjectType'];
export type ObjectTypeInput = Schemas['ObjectTypeInput'];
export type ObjectGroup = Schemas['ObjectGroup'];
export type ObjectGroupInput = Schemas['ObjectGroupInput'];
export type StatusDefinition = Schemas['StatusDefinition'];
export type StatusDefinitionInput = Schemas['StatusDefinitionInput'];

export type MapObject = Schemas['MapObject'];
export type MapObjectInput = Schemas['MapObjectInput'];
export type MapObjectPage = Schemas['MapObjectPage'];

export type RetractRequest = Schemas['RetractRequest'];
export type RetractResult = Schemas['RetractResult'];
export type BulkRetractRequest = Schemas['BulkRetractRequest'];
export type BulkRetractResult = Schemas['BulkRetractResult'];
export type ComplaintRequest = Schemas['ComplaintRequest'];
export type ComplaintCreated = Schemas['ComplaintCreated'];

export type ModerationCase = Schemas['ModerationCase'];
export type ModerationCaseSummary = Schemas['ModerationCaseSummary'];
export type ModerationCasePage = Schemas['ModerationCasePage'];
export type ModerationCaseKind = Schemas['ModerationCaseKind'];
export type ModerationCaseState = Schemas['ModerationCaseState'];
export type ApproveCaseRequest = Schemas['ApproveCaseRequest'];
export type RejectCaseRequest = Schemas['RejectCaseRequest'];

export type AdminUser = Schemas['AdminUser'];
export type AdminUserPage = Schemas['AdminUserPage'];
export type SetRolesRequest = Schemas['SetRolesRequest'];
export type RoleName = Schemas['RoleName'];
export type AuditEntry = Schemas['AuditEntry'];
export type AuditPage = Schemas['AuditPage'];
export type GlobalSettings = Schemas['GlobalSettings'];
export type GlobalSettingsPatch = Schemas['GlobalSettingsPatch'];

export type SecretMeta = Schemas['SecretMeta'];
export type SecretKind = Schemas['SecretKind'];
export type TelegramSessionSecret = Schemas['TelegramSessionSecret'];
export type ProxySecret = Schemas['ProxySecret'];
export type LlmSecret = Schemas['LlmSecret'];
