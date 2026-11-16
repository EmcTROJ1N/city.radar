import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { describe, expect, it } from 'vitest';
import { ApiError, errorText, toApiError, unhandledFieldErrors } from './api-error';

describe('toApiError', () => {
  it('разбирает problem+json с ошибками полей', () => {
    const response = new HttpErrorResponse({
      status: 422,
      error: {
        type: 'about:blank',
        title: 'Ошибка валидации',
        status: 422,
        detail: 'Запрос содержит недопустимые значения',
        errors: [
          { field: 'sourceStatusId', message: 'Статус не принадлежит выбранному источнику' },
          { field: 'comment', message: 'Слишком длинный комментарий' },
        ],
      },
    });

    const error = toApiError(response);

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(422);
    expect(error.title).toBe('Ошибка валидации');
    expect(error.detail).toBe('Запрос содержит недопустимые значения');
    expect(error.fieldErrors).toEqual({
      sourceStatusId: 'Статус не принадлежит выбранному источнику',
      comment: 'Слишком длинный комментарий',
    });
    expect(errorText(error)).toBe('Запрос содержит недопустимые значения');
  });

  it('при status 0 пишет «Нет связи с сервером»', () => {
    const error = toApiError(new HttpErrorResponse({ status: 0 }));
    expect(error.status).toBe(0);
    expect(error.title).toBe('Нет связи с сервером');
  });

  it('для 404 без тела берёт подпись по коду', () => {
    const error = toApiError(new HttpErrorResponse({ status: 404 }));
    expect(error.title).toBe('Не найдено');
    expect(error.detail).toBeNull();
  });

  it('читает Retry-After', () => {
    const response = new HttpErrorResponse({
      status: 429,
      headers: new HttpHeaders({ 'Retry-After': '30' }),
      error: { title: 'Слишком много запросов', status: 429 },
    });
    expect(toApiError(response).retryAfter).toBe(30);
  });

  it('оборачивает не-HttpErrorResponse в ApiError со статусом 0', () => {
    const error = toApiError(new Error('Сломано'));
    expect(error.status).toBe(0);
    expect(error.title).toBe('Сломано');
  });
});

describe('unhandledFieldErrors', () => {
  it('возвращает поля, для которых на форме нет своего сообщения', () => {
    const error = new ApiError(422, 'Ошибка валидации', null, {
      name: 'Название обязательно',
      code: 'Код занят',
    });

    expect(unhandledFieldErrors(error, ['name'])).toEqual(['code']);
    expect(unhandledFieldErrors(error, ['name', 'code'])).toEqual([]);
    expect(unhandledFieldErrors(new ApiError(500, 'Ошибка сервера'), [])).toEqual([]);
  });
});
