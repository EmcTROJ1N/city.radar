import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Permission } from '@shared/lib/permissions';
import { SessionService } from './session.service';

export const permissionGuard = (...anyOf: Permission[]): CanActivateFn => {
  return async (_route, state) => {
    const session = inject(SessionService);
    if (!session.ready()) await session.init();
    if (session.hasAny(anyOf)) return true;
    return inject(Router).createUrlTree(['/login'], {
      queryParams: { returnUrl: state.url },
    });
  };
};
