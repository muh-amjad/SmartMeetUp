import { Injectable } from '@angular/core';

export type LogLevel = 'DEBUG' | 'INFO' | 'WARN' | 'ERROR';

@Injectable({
  providedIn: 'root', // globally available
})
export class LoggingService {

  private formatMessage(level: LogLevel, message: any, data?: any) {
    const timestamp = new Date().toISOString();
    if (data !== undefined) {
      return `[${timestamp}] [${level}] ${message} - Data: ${JSON.stringify(data)}`;
    }
    return `[${timestamp}] [${level}] ${message}`;
  }

  debug(message: string, data?: any) {
    console.debug(this.formatMessage('DEBUG', message, data));
  }

  info(message: string, data?: any) {
    console.info(this.formatMessage('INFO', message, data));
  }

  warn(message: string, data?: any) {
    console.warn(this.formatMessage('WARN', message, data));
  }

  error(message: string, data?: any) {
    console.error(this.formatMessage('ERROR', message, data));
  }
}
