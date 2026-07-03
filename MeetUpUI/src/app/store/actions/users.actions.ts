export const UPDATE_USERS_LIST = '[User] Update Users List';
export const ADD_USER = '[User] ADD_USER';
export const REMOVE_USER = '[User] REMOVE_USER';
export const SET_LOADING = '[User] Set Loading';

import { createAction, props } from '@ngrx/store';
import { User } from '../../models/user.model';

export const addUser = createAction(ADD_USER, props<{ user: User }>());

export const removeUser = createAction(REMOVE_USER, props<{ user: User }>());

export const setLoading = createAction(SET_LOADING, props<{ loading: boolean }>());

export const updateUserList = createAction(UPDATE_USERS_LIST, props<{ users: User[] }>());
