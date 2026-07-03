export class CallOfferDto {
  from: string;
  to: string;
  roomId: string;
  offer: RTCSessionDescriptionInit;
  fromUsername?: string;
  toUsername?: string;

  constructor(from: string, to: string, roomId: string, offer: RTCSessionDescriptionInit) {
    this.from = from;
    this.to = to;
    this.roomId = roomId;
    this.offer = offer;
  }
}