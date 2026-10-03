/**
 * Router state that opens the meeting page (`/meet`) without a meeting id, telling it what to
 * set up. A meeting page with neither a meeting id nor this state has nothing to show, and
 * sends the user back to the dashboard.
 */
export type CallPageState =
  /** A fresh meeting, from the camera check ("Start meeting"). */
  | { source: 'join-now' }
  /** A fresh meeting that rings one person straight away ("Call" on the dashboard). */
  | { source: 'call'; invitee: { connectionId: string; username: string } };

export function readCallPageState(state: unknown): CallPageState | null {
  const value = state as Partial<{ source: string; invitee: { connectionId?: unknown; username?: unknown } }> | null;
  if (value?.source === 'join-now') {
    return { source: 'join-now' };
  }
  if (
    value?.source === 'call' &&
    typeof value.invitee?.connectionId === 'string' &&
    typeof value.invitee?.username === 'string'
  ) {
    return {
      source: 'call',
      invitee: { connectionId: value.invitee.connectionId, username: value.invitee.username },
    };
  }
  return null;
}
