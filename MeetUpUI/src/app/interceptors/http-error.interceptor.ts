import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { ToastService } from '../services/toast.service';

/**
 * Functional HTTP interceptor. Parses RFC 7807 `ProblemDetails` bodies
 * returned by the API and pushes a toast so the user always sees a message.
 * The error is re-thrown so caller-side handlers can still react.
 */
export const httpErrorInterceptor: HttpInterceptorFn = (request, next) => {
  const toastService = inject(ToastService);

  return next(request).pipe(
    catchError((error: HttpErrorResponse) => {
      let errorMessage = 'An error occurred';

      const problemDetails = error.error as { title?: string; detail?: string } | null;
      if (problemDetails?.detail) {
        errorMessage = problemDetails.detail;
      } else if (problemDetails?.title) {
        errorMessage = problemDetails.title;
      } else if (error.message) {
        errorMessage = error.message;
      }

      toastService.error(errorMessage);
      return throwError(() => error);
    }),
  );
};
