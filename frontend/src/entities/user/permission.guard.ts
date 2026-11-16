import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Permission } from '@shared/lib/permissions';
import { SessionService } from './session.service';

export const permissionGuard = (...anyOf: Permission[]): CanActivateFn => {
  return async (_route, state) => {
    // Оба inject() — до первого await: после await контекст внедрения теряется (NG0203).
    const session = inject(SessionService);
    const router = inject(Router);
    if (!session.ready()) await session.init();
    if (session.hasAny(anyOf)) return true;
    return router.createUrlTree(['/login'], {
      queryParams: { returnUrl: state.url },
    });
  };
};
