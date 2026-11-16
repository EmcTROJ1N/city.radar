import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';

const STATUS_TITLE: Record<number, string> = {
  400: 'Некорректный запрос',
  401: 'Нужен вход',
  403: 'Недостаточно прав',
  404: 'Не найдено',
  409: 'Конфликт',
  422: 'Ошибка валидации',
  429: 'Слишком много запросов',
  501: 'Не реализовано',
};

export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string | null;
  readonly fieldErrors: Record<string, string>;
  readonly retryAfter: number | null;

  constructor(
    status: number,
    title: string,
    detail: string | null = null,
    fieldErrors: Record<string, string> = {},
    retryAfter: number | null = null,
  ) {
    super(detail ?? title);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.fieldErrors = fieldErrors;
    this.retryAfter = retryAfter;
  }
}

function statusTitle(status: number): string {
  if (status >= 500 && status !== 501) return 'Ошибка сервера';
  return STATUS_TITLE[status] ?? 'Ошибка запроса';
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function parseBody(body: unknown): Record<string, unknown> | null {
  const direct = asRecord(body);
  if (direct) return direct;
  if (typeof body === 'string') {
    try {
      return asRecord(JSON.parse(body));
    } catch {
      return null;
    }
  }
  return null;
}

function readFieldErrors(problem: Record<string, unknown>): Record<string, string> {
  const result: Record<string, string> = {};
  const errors = problem['errors'];
  if (!Array.isArray(errors)) return result;
  for (const item of errors) {
    const entry = asRecord(item);
    const field = entry?.['field'];
    const message = entry?.['message'];
    if (typeof field === 'string' && typeof message === 'string') result[field] = message;
  }
  return result;
}

function readRetryAfter(headers: HttpHeaders | null | undefined): number | null {
  const value = headers?.get('Retry-After');
  if (!value) return null;
  const seconds = Number(value);
  return Number.isFinite(seconds) ? seconds : null;
}

export function toApiError(err: unknown): ApiError {
  if (err instanceof ApiError) return err;

  if (err instanceof HttpErrorResponse) {
    const problem = parseBody(err.error);
    const problemTitle = problem?.['title'];
    const problemDetail = problem?.['detail'];
    const title =
      err.status === 0
        ? 'Нет связи с сервером'
        : typeof problemTitle === 'string' && problemTitle
          ? problemTitle
          : statusTitle(err.status);
    const detail = typeof problemDetail === 'string' ? problemDetail : null;
    return new ApiError(
      err.status,
      title,
      detail,
      problem ? readFieldErrors(problem) : {},
      readRetryAfter(err.headers),
    );
  }

  const message =
    err instanceof Error ? err.message : typeof err === 'string' ? err : 'Неизвестная ошибка';
  return new ApiError(0, message);
}

export function errorText(e: ApiError): string {
  return e.detail ?? e.title;
}

/** Поля 422-й ошибки, для которых на форме нет своего сообщения. */
export function unhandledFieldErrors(e: ApiError, shown: string[]): string[] {
  return Object.keys(e.fieldErrors).filter((field) => !shown.includes(field));
}
