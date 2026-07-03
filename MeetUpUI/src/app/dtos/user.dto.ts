export class UserDto{
    username!: string;
    id!: string;
    appUserId?: string;
    email?: string;
    isInCall!: boolean;
    roomId?: string | null;

    constructor(username: string, id:string, appUserId?: string, email?: string, isInCall: boolean = false, roomId: string | null = null){
        this.username = username;
        this.id = id;
        this.appUserId = appUserId;
        this.email = email;
        this.isInCall = isInCall;
        this.roomId = roomId;
    }
}