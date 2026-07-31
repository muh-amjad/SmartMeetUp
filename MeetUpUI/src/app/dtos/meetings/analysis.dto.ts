export interface MeetingSummaryDto {
  overviewText: string;
  keyTopics: string[];
  providerKey: string;
  providerDisplayName: string;
  modelUsed: string;
  generatedUtc: string;
}

export type ActionItemStatus = 'Open' | 'Done' | 'Cancelled';

export interface ActionItemDto {
  id: string;
  meetingId: string;
  description: string;
  assigneeUserId: string | null;
  assigneeUsername: string | null;
  assigneeNameRaw: string | null;
  dueDateUtc: string | null;
  status: ActionItemStatus;
  completedUtc: string | null;
  sourceStartMs: number | null;
}

export interface UpdateActionItemRequestDto {
  description?: string;
  status?: ActionItemStatus;
  assigneeUserId?: string;
  dueDateUtc?: string | null;
  clearDueDate?: boolean;
}

export interface DecisionDto {
  id: string;
  description: string;
  sourceStartMs: number | null;
  createdUtc: string;
}

export interface FollowUpEmailDto {
  subject: string;
  bodyMarkdown: string;
  status: string;
  sentUtc: string | null;
  createdUtc: string;
}

export interface UpdateFollowUpEmailRequestDto {
  subject: string;
  bodyMarkdown: string;
}

export interface AnalysisProviderDto {
  key: string;
  displayName: string;
  isFree: boolean;
  isDefault: boolean;
  contextWindow: number;
}

export interface UserPreferencesDto {
  preferredAnalysisProviderKey: string | null;
  /** Omitted on a PATCH means "leave unchanged". */
  optOutFollowUpEmails?: boolean | null;
}

export interface FollowUpRecipientDto {
  userId: string;
  email: string;
  displayName: string;
}

export interface FollowUpRecipientsDto {
  recipients: FollowUpRecipientDto[];
  optedOutCount: number;
  /** False when the server has no email transport configured. */
  canSend: boolean;
}

export interface SpeakingShareDto {
  userId: string;
  displayName: string;
  seconds: number;
  percent: number;
}

export interface MeetingAnalyticsDto {
  totalDurationSeconds: number;
  participantCount: number;
  wordCount: number;
  averageWordsPerMinute: number;
  totalSpeakingSeconds: number;
  speakingDistribution: SpeakingShareDto[];
  computedUtc: string;
}

export interface WeeklyBucketDto {
  weekStartUtc: string;
  meetingCount: number;
  totalMinutes: number;
}

export type ActionItemFilter = 'all' | 'open' | 'done' | 'overdue' | 'mine';

export interface ActionItemWithMeetingDto {
  id: string;
  meetingId: string;
  meetingTitle: string;
  meetingDate: string;
  description: string;
  assigneeUserId: string | null;
  assigneeUsername: string | null;
  assigneeNameRaw: string | null;
  dueDateUtc: string | null;
  status: ActionItemStatus;
  isOverdue: boolean;
  isAssignedToMe: boolean;
  completedUtc: string | null;
}

export interface ProfileDto {
  userId: string;
  username: string;
  email: string;
  displayName: string;
}

export interface UpdateProfileRequestDto {
  displayName: string;
}

export interface ChangePasswordRequestDto {
  currentPassword: string;
  newPassword: string;
}

export interface AccountAnalyticsDto {
  totalMeetingHours: number;
  meetingCount: number;
  averageDurationMinutes: number;
  totalSpeakingSeconds: number;
  mostDiscussedTopics: string[];
  weeklyBreakdown: WeeklyBucketDto[];
}
