import { Injectable, inject } from '@angular/core';
import { Store } from '@ngrx/store';
import * as UsersActions from '../actions/users.actions';
import { usersFeature } from '../reducers/users.reducer';
import { User } from '../../models/user.model';

@Injectable({ providedIn: 'root' })
export class UsersFacade {
  private store = inject(Store);

  users = this.store.selectSignal(usersFeature.selectUsers);
  loading = this.store.selectSignal(usersFeature.selectLoading);

  addUser(user: User) {
    this.store.dispatch(UsersActions.addUser({ user }));
  }

  removeUser(user: User) {
    this.store.dispatch(UsersActions.removeUser({ user }));
  }

  setLoading(loading: boolean) {
    this.store.dispatch(UsersActions.setLoading({ loading }));
  }

  updateUserList(users: User[]) {
    this.store.dispatch(UsersActions.updateUserList({ users }));
  }
}
