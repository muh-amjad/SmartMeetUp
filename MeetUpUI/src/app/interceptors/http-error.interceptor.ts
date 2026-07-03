import { Injectable } from '@angular/core';
import {
  HttpInterceptor,
  HttpRequest,
  HttpHandler,
  HttpEvent,
  HttpErrorResponse
} from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { ToastService } from '../services/toast.service';

@Injectable()
export class HttpErrorInterceptor implements HttpInterceptor {
  constructor(private toastService: ToastService) {}

  intercept(
    request: HttpRequest<any>,
    next: HttpHandler
  ): Observable<HttpEvent<any>> {
    return next.handle(request).pipe(
      catchError((error: HttpErrorResponse) => {
        let errorMessage = 'An error occurred';

        if (error.error && error.error.title) {
          errorMessage = error.error.title;
          if (error.error.detail) {
            errorMessage = error.error.detail;
          }
        } else if (error.message) {
          errorMessage = error.message;
        }

        this.toastService.error(errorMessage);
        return throwError(() => error);
      })
    );
  }
}

export const httpErrorInterceptor = (req: HttpRequest<any>, next: HttpHandler) => {
  const toastService = new ToastService();
  return next.handle(req).pipe(
    catchError((error: HttpErrorResponse) => {
      let errorMessage = 'An error occurred';

      if (error.error && error.error.title) {
        errorMessage = error.error.title;
        if (error.error.detail) {
          errorMessage = error.error.detail;
        }
      } else if (error.message) {
        errorMessage = error.message;
      }

      toastService.error(errorMessage);
      return throwError(() => error);
    })
  );
};
