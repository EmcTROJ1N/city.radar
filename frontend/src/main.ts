import { bootstrapApplication } from '@angular/platform-browser';
import { applyUiConfig } from '@shared/config/apply-ui-config';
import { App } from './app/app';
import { appConfig } from './app/app.config';

applyUiConfig();

bootstrapApplication(App, appConfig).catch((err) => console.error(err));
