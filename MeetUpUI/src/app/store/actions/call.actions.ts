import { createAction, props } from '@ngrx/store';

export const UPDATE_CALL_STATE = '[Call] Update Call State';

export const updateCallState = createAction(UPDATE_CALL_STATE, props<{ isCallStarted: boolean }>());
